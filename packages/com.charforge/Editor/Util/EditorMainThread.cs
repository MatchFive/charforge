using System;
using System.Collections.Generic;
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

        static EditorMainThread()
        {
            EditorApplication.update += Drain;
        }

        /// <summary>把一段需要在主线程执行的操作排队。</summary>
        public static void Post(Action action)
        {
            if (action == null) return;
            lock (Gate) Queue.Enqueue(action);
            _draining = false;
        }

        private static void Drain()
        {
            if (_draining) return;
            _draining = true;

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

        /// <summary>等待主线程执行并返回结果（供 async 流程使用）。</summary>
        public static System.Threading.Tasks.Task<T> RunAsync<T>(Func<T> func)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
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
            var tcs = new System.Threading.Tasks.TaskCompletionSource<object>();
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
