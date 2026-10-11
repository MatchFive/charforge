using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace CharForge.EditorTools
{
    /// <summary>
    /// CharForge 主窗口：描述 → 参考图 → 生成 → 装配，全在 Editor 内完成。
    ///
    /// 交互设计与刻意的取舍：
    /// - **进度实时可见**：长任务最怕「点了没反应」，所以每 0.5 秒轮询一次并打印到日志区。
    /// - **人审检查点**：画风参考图需要你自己拖进来，不自动从项目里抓——
    ///   自动抓的结果不可控，反而要做两遍。
    /// - **产物可重新装配**：装配与生成解耦，改了配点重装即可，不用重新出图。
    /// </summary>
    public class CharacterForgeWindow : EditorWindow
    {
        private string _serverUrl = "http://127.0.0.1:8000";
        private string _prompt = "博物馆导览员，身着素雅青灰色长衫，手持讲解册，神态温和";
        private readonly List<Texture2D> _refs = new List<Texture2D>();
        private string _preset = "custom";
        private bool _actionIdle = true;
        private bool _actionWalk = true;
        private readonly bool[] _dirs = { true, true, false, false };
        private int _movementIndex = 2;
        private bool _dialogue = true;
        private int _framesPerClip = 4;

        private bool _busy;
        private float _progress;
        private string _status = "就绪";
        private string _lastOutputDir;
        private Vector2 _logScroll;
        private static readonly List<string> Log = new List<string>();

        private static readonly string[] DirNames = { "南（正面）", "北（背面）", "东（右）", "西（左）" };
        private static readonly string[] DirKeys = { "south", "north", "east", "west" };
        private static readonly string[] MovementKeys = { "none", "rigidbody", "astar", "navmesh" };
        private static readonly string[] MovementLabels = { "不移动", "Rigidbody2D 自由移动", "A* 寻路", "NavMesh（需 2D 方案）" };

        [MenuItem("Window/CharForge 角色资产生成")]
        public static void Open()
        {
            var win = GetWindow<CharacterForgeWindow>("CharForge");
            win.minSize = new Vector2(420, 560);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("CharForge · 2D 角色资产流水线", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "一句话 + 项目画风参考图 → 可直接拖进场景的 2D NPC prefab。\n" +
                "服务端默认地址 http://127.0.0.1:8000，先运行：python -m server.cli serve",
                MessageType.None);

            EditorGUILayout.Space(4);
            _serverUrl = EditorGUILayout.TextField("服务端地址", _serverUrl);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("角色描述", EditorStyles.boldLabel);
            _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.MinHeight(48));

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("画风参考图（建议 3-5 张，人审）", EditorStyles.boldLabel);
            int newCount = Mathf.Max(1, EditorGUILayout.IntField("数量", Mathf.Max(1, _refs.Count)));
            while (_refs.Count < newCount) _refs.Add(null);
            while (_refs.Count > newCount && _refs.Count > 1) _refs.RemoveAt(_refs.Count - 1);
            for (int i = 0; i < _refs.Count; i++)
            {
                _refs[i] = (Texture2D)EditorGUILayout.ObjectField($"参考图 {i + 1}", _refs[i], typeof(Texture2D), false);
            }

            _preset = EditorGUILayout.TextField("风格预设", _preset);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("方向与动作", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            _actionIdle = EditorGUILayout.ToggleLeft("待机 idle", _actionIdle, GUILayout.Width(100));
            _actionWalk = EditorGUILayout.ToggleLeft("行走 walk", _actionWalk, GUILayout.Width(100));
            _framesPerClip = EditorGUILayout.IntSlider("每片段帧数", _framesPerClip, 2, 8);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            for (int i = 0; i < DirNames.Length; i++)
                _dirs[i] = EditorGUILayout.ToggleLeft(DirNames[i], _dirs[i], GUILayout.Width(96));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(6);
            _movementIndex = EditorGUILayout.Popup("移动方式", _movementIndex, MovementLabels);
            _dialogue = EditorGUILayout.Toggle("启用对话人设", _dialogue);

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(_busy))
            {
                if (GUILayout.Button(_busy ? "生成中…" : "生成并装配", GUILayout.Height(32)))
                {
                    EditorCoroutineUtility.StartCoroutineOwnerless(GenerateRoutine());
                }
            }

            using (new EditorGUI.DisabledScope(_busy || string.IsNullOrEmpty(_lastOutputDir)))
            {
                if (GUILayout.Button("仅重新装配（用上次的产物）"))
                {
                    var report = PrefabAssembler.Assemble(_lastOutputDir);
                    Log.Add(report.ok ? $"重新装配成功：{report.prefabAssetPath}" : "重新装配失败");
                }
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField($"状态：{_status}");
            if (_busy)
            {
                var rect = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(rect, Mathf.Clamp01(_progress / 100f), $"{_progress:0.0}%");
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("日志", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.MinHeight(120));
            foreach (var line in Log.Skip(Mathf.Max(0, Log.Count - 200)))
                EditorGUILayout.LabelField(line, EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("清空日志")) Log.Clear();
        }

        private JObject BuildRequest()
        {
            var actions = new List<string>();
            if (_actionIdle) actions.Add("idle");
            if (_actionWalk) actions.Add("walk");

            var dirs = new List<string>();
            for (int i = 0; i < DirKeys.Length; i++)
                if (_dirs[i]) dirs.Add(DirKeys[i]);
            if (dirs.Count == 0) dirs.Add("south");

            var refs = _refs
                .Where(t => t != null)
                .Select(t => AssetDatabase.GetAssetPath(t))
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => Path.GetFullPath(p))
                .ToList();

            return new JObject
            {
                ["prompt"] = _prompt,
                ["reference_images"] = new JArray(refs),
                ["style_preset"] = string.IsNullOrEmpty(_preset) ? "custom" : _preset,
                ["actions"] = new JArray(actions),
                ["directions"] = new JArray(dirs),
                ["movement"] = new JObject
                {
                    ["mode"] = MovementKeys[Mathf.Clamp(_movementIndex, 0, MovementKeys.Length - 1)],
                    ["speed"] = 1.5,
                    ["patrol_points"] = new JArray()
                },
                ["dialogue_enabled"] = _dialogue,
                ["forbidden"] = new JArray()
            };
        }

        private IEnumerator GenerateRoutine()
        {
            _busy = true;
            _progress = 0f;
            _status = "提交任务…";
            Log.Add($"--- 新任务 {System.DateTime.Now:HH:mm:ss} ---");

            string jobId = null;

            // 1) 建任务
            byte[] body = Encoding.UTF8.GetBytes(BuildRequest().ToString());
            using (var www = new UnityWebRequest($"{_serverUrl}/api/jobs", "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(body);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Fail($"提交失败：{www.error}");
                    yield break;
                }
                jobId = (string)JObject.Parse(www.downloadHandler.text)["job_id"];
            }
            Log.Add($"任务号 {jobId}");

            // 2) 轮询进度
            bool finished = false;
            string outputDir = null;
            int guard = 0;

            while (!finished && guard++ < 1800)
            {
                yield return new EditorWaitForSeconds(0.5f);

                using (var www = UnityWebRequest.Get($"{_serverUrl}/api/jobs/{jobId}"))
                {
                    yield return www.SendWebRequest();
                    long code = www.responseCode;

                    if (code == 202)                       // 进行中
                    {
                        _status = "生成中…";
                        _progress = Mathf.Min(_progress + 1.2f, 95f);
                        continue;
                    }
                    if (code != 200)
                    {
                        Fail($"查询任务失败：HTTP {code} {www.error}");
                        yield break;
                    }

                    var result = JObject.Parse(www.downloadHandler.text);
                    bool ok = result["ok"] != null && (bool)result["ok"];
                    outputDir = (string)result["output_dir"];

                    if (!ok)
                    {
                        Fail($"任务失败：{result["error"]}");
                        yield break;
                    }

                    foreach (var w in result["warnings"] as JArray ?? new JArray())
                        Log.Add($"  提醒：{w}");

                    finished = true;
                }
            }

            if (!finished)
            {
                Fail("超时：任务长时间未结束");
                yield break;
            }

            _progress = 100f;
            _status = "生成完成，开始装配…";
            _lastOutputDir = outputDir;
            Log.Add($"产物目录 {outputDir}");

            // 3) 装配 prefab
            var report = PrefabAssembler.Assemble(outputDir);
            foreach (var m in report.messages) Log.Add($"  {m}");

            if (report.ok)
            {
                _status = $"完成 → {report.prefabAssetPath}";
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(report.prefabAssetPath);
                if (prefab != null)
                {
                    EditorGUIUtility.PingObject(prefab);
                    Selection.activeObject = prefab;
                }
            }
            else
            {
                _status = "装配失败，请看日志";
            }

            _busy = false;
            Repaint();
        }

        private void Fail(string message)
        {
            _status = message;
            Log.Add(message);
            _busy = false;
            Repaint();
        }
    }
}
