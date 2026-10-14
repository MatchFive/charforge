using System;
using System.Collections.Generic;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace CharForge.EditorTools
{
    /// <summary>
    /// 把后台线程的完成回调送回编辑器主线程。
    ///
    /// 为什么需要它：本插件大量使用 <c>async/await</c> + <c>HttpClient</c> 做长耗时请求，
    /// 而 Unity 的 Editor / AssetDatabase / GameObject API **只能在主线程调用**。
    /// 直接在 <c>Task</c> 回调里碰 AssetDatabase 会得到随机崩溃——
    /// 这是编辑器工具最常见、也最难复现的一类 bug。
    ///
    /// 用法：后台线程里调用 <see cref="Post"/>，主线程在 EditorApplication.update 中排空队列。
    /// </summary>
    [InitializeOnLoad]
    public static class EditorMainThread
    {
        private static readonly Queue<Action> Queue = new Queue<Action>();
        private static readonly object Gate = new object();
        private static volatile bool _draining;

        /// <summary>
        /// 主线程 id，在静态构造里捕获。<c>[InitializeOnLoad]</c> 保证那一刻在主线程，
        /// 此后任何时候都能拿它判定「我现在在不在主线程」。
        /// </summary>
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;

        static EditorMainThread()
        {
            EditorApplication.update += Drain;
        }

        /// <summary>当前是否在编辑器主线程。</summary>
        public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == MainThreadId;

        /// <summary>
        /// 断言当前在主线程；不在就抛出**指名道姓**的异常。
        ///
        /// 为什么值得单独写一个：Unity 自己那句话的后半截——
        /// 「Constructors and field initializers will be executed from the loading thread
        /// when loading a scene」——讲的是**另一个场景**，读起来像「构造器 / 字段初始化写错了」，
        /// 而真实原因通常是「这段代码被 <c>ConfigureAwait(false)</c> 甩到了线程池」。
        /// v0.4.5 实测为此白跑过一轮排查，所以宁可自己抛一句真话。
        /// </summary>
        public static void EnsureMainThread(string what)
        {
            if (IsMainThread) return;
            throw new InvalidOperationException(
                $"{what} 只能在主线程调用，当前在线程池线程上。" +
                "Unity 的纹理 / AssetDatabase / GameObject API 都不允许跨线程——" +
                "请把它包进 EditorMainThread.RunAsync(...)（或让所在方法自封送）再调用。");
        }

        /// <summary>把一段需要在主线程执行的操作排队。</summary>
        public static void Post(Action action)
        {
            if (action == null) return;
            lock (Gate) Queue.Enqueue(action);
        }

        private static void Drain()
        {
            if (_draining) return;
            _draining = true;

            try
            {
                // 单帧最多处理 64 个回调：宁可分帧，也不要让编辑器卡住。
                for (int i = 0; i < 64; i++)
                {
                    Action action;
                    lock (Gate)
                    {
                        if (Queue.Count == 0) break;
                        action = Queue.Dequeue();
                    }

                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[CharForge] 主线程回调抛出异常：{e}");
                    }
                }
            }
            finally
            {
                // 守卫必须在本帧结束时复位：Drain 由 EditorApplication.update 每帧触发，
                // 若不复位，后续排队的回调在本帧结束前将永远得不到执行。
                _draining = false;
            }
        }

        /// <summary>
        /// 等待主线程执行并返回结果（供 async 流程使用）。
        ///
        /// <c>RunContinuationsAsynchronously</c> 不能省：否则 <c>SetResult</c> 会在
        /// Drain 的循环里**同步**跑完 await 之后的整段流水线（可能是几十秒的出图与装配），
        /// 编辑器会整帧卡死，而且会重入 Drain。
        /// </summary>
        public static System.Threading.Tasks.Task<T> RunAsync<T>(Func<T> func)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }

        /// <summary>等待主线程执行（无返回值）。</summary>
        public static System.Threading.Tasks.Task RunAsync(Action action)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<object>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(null);
                }
                catch (Exception e) { tcs.SetException(e); }
            });
            return tcs.Task;
        }
    }
}
