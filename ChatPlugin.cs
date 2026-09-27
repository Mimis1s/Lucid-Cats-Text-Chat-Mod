using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LucidCatsChat
{
    [BepInPlugin("lucidcats.chat", "Lucid Cats 文字聊天", "1.0.0")]
    public class ChatPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("=== Lucid Cats 文字聊天插件启动 ===");
            var go = new GameObject("LucidCatsChat_Manager");
            DontDestroyOnLoad(go);
            var mgr = go.AddComponent<ChatManager>();
            ChatManager.Instance = mgr;
        }
    }

    public class ChatManager : MonoBehaviour
    {
        private readonly List<string> _messages = new List<string>();
        private const int MaxMessages = 50;

        private bool _typing;
        private string _input = "";

        // 静态实例，供 ChatNetwork 回调使用
        internal static ChatManager Instance;

        // 淡出控制
        private float _visibleUntil;      // 聊天框保持可见到的时间点
        private const float FadeDuration = 1.5f;  // 淡出时长（秒）
        private const float HoldDuration = 5f;    // 发消息后保持可见的时长（秒）

        // 布局（左中，窄长）
        private const float MarginLeft = 24f;
        private const float BoxWidth = 300f;
        private const float BoxHeight = 420f;
        private const float InputHeight = 32f;
        private const int FontSize = 18;

        private GUIStyle _msgStyle;
        private GUIStyle _inputStyle;
        private GUIStyle _hintStyle;
        private Font _cnFont;
        private bool _fontReady;
        private Texture2D _bgTex;   // 浅灰半透明底纹

        private Keyboard _subscribedKb;

        // 记录被我们禁用的 PlayerInput，用于恢复
        private readonly List<UnityEngine.InputSystem.PlayerInput> _disabledInputs =
            new List<UnityEngine.InputSystem.PlayerInput>();

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            // 确保订阅文本输入（一直保持）
            TrySubscribeTextInput(kb);

            // 只在游戏场景（GameScene）响应聊天
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "GameScene")
                return;

            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                if (!_typing)
                {
                    _typing = true;
                    _input = "";
                    DisableGameInput();
                }
                else
                {
                    SendMessage();
                }
            }
            else if (kb.escapeKey.wasPressedThisFrame && _typing)
            {
                _typing = false;
                _input = "";
                RestoreGameInput();
            }

            if (_typing && kb.backspaceKey.wasPressedThisFrame && _input.Length > 0)
            {
                _input = _input.Substring(0, _input.Length - 1);
            }

            // 打字时强制开启 IME（诊断中文输入）
            if (_typing)
            {
                TryEnableIME(kb);
            }
        }

        private void TryEnableIME(Keyboard kb)
        {
            // 中文输入关键：强制开启 IME（旧 API + 新 Input System 双保险）
            try
            {
                if (Input.imeCompositionMode != IMECompositionMode.On)
                    Input.imeCompositionMode = IMECompositionMode.On;
            }
            catch (Exception) { }
            try
            {
                if (kb != null)
                    kb.SetIMEEnabled(true);
            }
            catch (Exception) { }
        }

        // 聊天打开时禁用游戏输入，防止误触移动等操作
        private void DisableGameInput()
        {
            try
            {
                var inputs = UnityEngine.Object.FindObjectsOfType<UnityEngine.InputSystem.PlayerInput>();
                foreach (var pi in inputs)
                {
                    if (pi == null) continue;
                    if (pi.enabled)
                    {
                        pi.enabled = false;
                        _disabledInputs.Add(pi);
                    }
                }
                ChatPlugin.Log.LogInfo("[聊天] 已禁用游戏输入 " + _disabledInputs.Count + " 个");
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 禁用游戏输入失败: " + e.Message);
            }
        }

        private void RestoreGameInput()
        {
            foreach (var pi in _disabledInputs)
            {
                if (pi != null) pi.enabled = true;
            }
            _disabledInputs.Clear();
        }

        private void TrySubscribeTextInput(Keyboard kb)
        {
            if (_subscribedKb == kb) return;
            if (_subscribedKb != null)
            {
                try { _subscribedKb.onTextInput -= OnTextInput; } catch (Exception) { }
            }
            try
            {
                kb.onTextInput += OnTextInput;
                _subscribedKb = kb;
                ChatPlugin.Log.LogInfo("已订阅键盘文本输入事件");
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("订阅文本输入失败: " + e.Message);
            }
        }

        private void OnTextInput(char c)
        {
            if (!_typing) return;
            if (c == '\n' || c == '\r' || c == '\b' || c == '\t' || c == 0 || c == 27) return;
            _input += c;
        }

        private void SendMessage()
        {
            var text = _input.Trim();
            if (string.IsNullOrEmpty(text))
            {
                _typing = false;
                _input = "";
                RestoreGameInput();
                return;
            }
            var name = GetPlayerName();
            var full = name + ": " + text;
            // 联网时只靠网络回显显示一次（避免主机收到自己的广播导致重复）；
            // 未联网（单人）时才本地显示。
            bool sent = ChatNetwork.Broadcast(full);
            if (!sent)
            {
                AddMessage(full);
            }
            _input = "";
            _typing = false;
            RestoreGameInput();
        }

        private string GetPlayerName()
        {
            try
            {
                var pn = PlayerName_Accessor.GetLocalPlayerName();
                if (!string.IsNullOrEmpty(pn)) return pn;
            }
            catch (Exception) { }
            return HasCNLocalization() ? "玩家" : "Player";
        }

        private void AddMessage(string msg)
        {
            _messages.Add(msg);
            while (_messages.Count > MaxMessages)
                _messages.RemoveAt(0);
            // 发消息后保持可见一段时间，然后淡出
            _visibleUntil = Time.unscaledTime + HoldDuration;
        }

        // 供 ChatNetwork 接收远程消息时调用
        internal static void AddRemoteMessage(string msg)
        {
            if (Instance != null)
            {
                Instance.AddMessage(msg);
            }
        }

        private void EnsureFont()
        {
            if (_fontReady) return;
            try
            {
                string path = Path.Combine(Paths.PluginPath, "cnfont.ttf");
                if (File.Exists(path))
                {
                    _cnFont = new Font(path);
                }
                else
                {
                    _cnFont = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", FontSize);
                }
                if (_cnFont != null)
                {
                    _fontReady = true;
                    ChatPlugin.Log.LogInfo("聊天中文字体加载成功");
                }
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("聊天字体加载失败: " + e.Message);
            }
        }

        private void EnsureStyles()
        {
            if (_msgStyle == null)
            {
                _msgStyle = new GUIStyle(GUI.skin.label);
                _msgStyle.fontSize = FontSize;
                _msgStyle.wordWrap = true;
                _msgStyle.alignment = TextAnchor.UpperLeft;
                _msgStyle.normal.textColor = Color.white;
                if (_cnFont != null) _msgStyle.font = _cnFont;

                _inputStyle = new GUIStyle(GUI.skin.textField);
                _inputStyle.fontSize = FontSize;
                if (_cnFont != null) _inputStyle.font = _cnFont;

                _hintStyle = new GUIStyle(GUI.skin.label);
                _hintStyle.fontSize = 14;
                _hintStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f, 0.9f);
                if (_cnFont != null) _hintStyle.font = _cnFont;
            }
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0.15f, 0.15f, 0.15f, 0.35f));
                _bgTex.Apply();
            }
        }

        private void OnGUI()
        {
            // 只在游戏场景显示
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "GameScene") return;

            // 计算整体透明度
            float alpha;
            if (_typing)
            {
                alpha = 1f;   // 打字时完全可见
            }
            else
            {
                float remain = _visibleUntil - Time.unscaledTime;
                if (remain <= 0f)
                {
                    return;   // 完全隐藏
                }
                alpha = Mathf.Clamp01(remain / FadeDuration);  // 从 1 淡出到 0
            }

            EnsureFont();
            EnsureStyles();

            // 用 GUI.color 控制整体透明度
            var oldColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            // 左中位置
            float totalHeight = BoxHeight + InputHeight + 6f;
            float boxY = (Screen.height - totalHeight) / 2f;
            float inputY = boxY + BoxHeight + 6f;

            // 消息列表（每行文字垫浅灰底，无大块背景框）
            float msgY = boxY + 8f;
            int start = Math.Max(0, _messages.Count - 10);
            for (int i = start; i < _messages.Count; i++)
            {
                float h = _msgStyle.CalcHeight(new GUIContent(_messages[i]), BoxWidth - 24f);
                GUI.DrawTexture(new Rect(MarginLeft, msgY, BoxWidth, h + 2f), _bgTex);
                GUI.Label(new Rect(MarginLeft + 12f, msgY + 1f, BoxWidth - 24f, h), _messages[i], _msgStyle);
                msgY += h + 4f;
            }

            // 输入框（只在打字时显示）
            if (_typing)
            {
                // 把输入法候选框定位到输入框位置（屏幕坐标，左上角原点）
                try
                {
                    var kb2 = Keyboard.current;
                    if (kb2 != null)
                        kb2.SetIMECursorPosition(new Vector2(MarginLeft + 10f, inputY + InputHeight));
                }
                catch (Exception) { }

                GUI.DrawTexture(new Rect(MarginLeft, inputY, BoxWidth, InputHeight), _bgTex);
                string display = _input;
                if (Time.unscaledTime % 1f < 0.5f)
                {
                    display += "|";
                }
                // 输入为空时显示占位提示（默认英文，检测到汉化包则用中文）
                if (string.IsNullOrEmpty(_input))
                {
                    var hint = HasCNLocalization() ? "按回车发送..." : "Press Enter to send...";
                    GUI.Label(new Rect(MarginLeft + 10f, inputY + 4f, BoxWidth - 20f, InputHeight - 4f), hint, _hintStyle);
                }
                else
                {
                    GUI.Label(new Rect(MarginLeft + 10f, inputY + 4f, BoxWidth - 20f, InputHeight - 4f), display, _inputStyle);
                }
            }

            GUI.color = oldColor;
        }

        // 检测是否安装了汉化包（lucidcats.cn）
        private static bool? _hasCNLocalization;
        private static bool HasCNLocalization()
        {
            if (_hasCNLocalization.HasValue) return _hasCNLocalization.Value;
            bool found = false;
            try
            {
                foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                {
                    if (info.Metadata.GUID == "lucidcats.cn") { found = true; break; }
                }
            }
            catch (Exception) { }
            _hasCNLocalization = found;
            return found;
        }
    }

    // 反射访问游戏玩家名
    internal static class PlayerName_Accessor
    {
        private static System.Reflection.PropertyInfo _localNameProp;
        private static System.Reflection.MethodInfo _getter;
        private static bool _resolved;

        internal static string GetLocalPlayerName()
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    Resolve();
                }
                if (_getter != null)
                {
                    object target = null;
                    if (!_getter.IsStatic)
                    {
                        // 实例方法：需要找场景里的 PlayerName 实例
                        target = FindInstance();
                    }
                    var v = _getter.Invoke(target, null);
                    var s = v?.ToString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 读取玩家名失败: " + e.Message);
            }
            return null;
        }

        private static void Resolve()
        {
            // 第一遍：优先找静态属性（静态 = 本地玩家，最可靠）
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (Exception) { continue; }

                foreach (var t in types)
                {
                    if (!t.Name.Contains("PlayerName") && !t.Name.Contains("Player")) continue;
                    foreach (var propName in new[] { "LocalPlayerName", "PlayerName", "DisplayName" })
                    {
                        var prop = t.GetProperty(propName,
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Static);
                        if (prop != null && prop.PropertyType == typeof(string))
                        {
                            var getter = prop.GetGetMethod(true);
                            if (getter != null && getter.IsStatic)
                            {
                                _localNameProp = prop;
                                _getter = getter;
                                ChatPlugin.Log.LogInfo("[聊天] 找到静态玩家名属性: " + t.FullName + "." + propName);
                                return;
                            }
                        }
                    }
                }
            }

            // 第二遍：退回实例属性
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (Exception) { continue; }

                foreach (var t in types)
                {
                    if (!t.Name.Contains("PlayerName") && !t.Name.Contains("Player")) continue;
                    foreach (var propName in new[] { "LocalPlayerName", "PlayerName", "DisplayName" })
                    {
                        var prop = t.GetProperty(propName,
                            System.Reflection.BindingFlags.Public |
                            System.Reflection.BindingFlags.NonPublic |
                            System.Reflection.BindingFlags.Instance);
                        if (prop != null && prop.PropertyType == typeof(string))
                        {
                            var getter = prop.GetGetMethod(true);
                            if (getter != null)
                            {
                                _localNameProp = prop;
                                _getter = getter;
                                ChatPlugin.Log.LogInfo("[聊天] 找到实例玩家名属性: " + t.FullName + "." + propName);
                                return;
                            }
                        }
                    }
                }
            }
            ChatPlugin.Log.LogWarning("[聊天] 未找到玩家名属性");
        }

        private static object FindInstance()
        {
            try
            {
                var targetType = _localNameProp != null ? _localNameProp.DeclaringType : null;
                if (targetType == null) return null;

                var all = new List<object>();
                try
                {
                    var objs = UnityEngine.Object.FindObjectsOfType(targetType);
                    if (objs != null) all.AddRange(objs);
                }
                catch (Exception) { }
                try
                {
                    var objs = Resources.FindObjectsOfTypeAll(targetType);
                    if (objs != null) all.AddRange(objs);
                }
                catch (Exception) { }

                if (all.Count == 0) return null;

                // 找 IsLocalPlayer / IsOwner 布尔属性，用于识别本地玩家
                foreach (var flagName in new[] { "IsLocalPlayer", "IsOwner", "IsLocal", "IsMine" })
                {
                    var flagProp = targetType.GetProperty(flagName,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    if (flagProp == null || flagProp.PropertyType != typeof(bool)) continue;

                    foreach (var o in all)
                    {
                        try
                        {
                            if (o != null && (bool)flagProp.GetValue(o))
                            {
                                ChatPlugin.Log.LogInfo("[聊天] 用 " + flagName + " 找到本地玩家实例");
                                return o;
                            }
                        }
                        catch (Exception) { }
                    }
                }

                // 退回第一个
                return all[0];
            }
            catch (Exception) { }
            return null;
        }
    }

    // 联机消息广播（用 NGO CustomMessagingManager）
    internal static class ChatNetwork
    {
        private delegate void ReadByteSafeDel(out byte value);
        private delegate void ReadBytesSafeDel(byte[] buf, int offset, int count);
        private delegate void WriteBytesSafeDel(byte[] buf, int offset, int count);
        private delegate void WriteByteSafeDel(byte value);
        private const string MsgName = "LucidCatsChat";

        private static bool _initialized;
        private static bool _ctorDumped;
        private static Type _writerType;
        private static Type _readerType;
        private static Type _deliveryType;
        private static Type _delegateType;
        private static object _networkManager;
        private static object _messagingManager;
        private static System.Reflection.MethodInfo _sendToAll;
        private static System.Reflection.MethodInfo _sendToServer;
        private static System.Reflection.MethodInfo _registerHandler;
        private static object _deliveryReliable;
        private static System.Delegate _handlerDelegate;

        internal static bool Broadcast(string message)
        {
            try
            {
                if (!TryInit())
                {
                    ChatPlugin.Log.LogWarning("[聊天] 网络未初始化，消息仅本地显示");
                    return false;
                }
                if (!IsConnected())
                {
                    DumpNetworkState();
                    ChatPlugin.Log.LogWarning("[聊天] 未连接（IsConnected=false），消息仅本地");
                    return false;
                }

                var writer = CreateWriter(message);
                if (writer == null)
                {
                    ChatPlugin.Log.LogWarning("[聊天] writer 创建失败");
                    return false;
                }

                bool isServer = IsServer();
                ChatPlugin.Log.LogInfo("[聊天] 发送消息: isServer=" + isServer + " clientId=" + GetServerClientId());
                if (isServer)
                {
                    _sendToAll.Invoke(_messagingManager, new[] { MsgName, writer, _deliveryReliable });
                }
                else
                {
                    _sendToServer.Invoke(_messagingManager, new[] { MsgName, GetServerClientId(), writer, _deliveryReliable });
                }
                ChatPlugin.Log.LogInfo("[聊天] 消息已发送");
                DisposeWriter(writer);
                return true;
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 广播失败: " + e);
                return false;
            }
        }

        private static bool TryInit()
        {
            if (_initialized) return _messagingManager != null;
            _initialized = true;

            try
            {
                _writerType = FindType("Unity.Netcode.FastBufferWriter");
                _readerType = FindType("Unity.Netcode.FastBufferReader");
                _deliveryType = FindType("Unity.Netcode.NetworkDelivery");
                _delegateType = FindType("Unity.Netcode.CustomMessagingManager+HandleNamedMessageDelegate");
                if (_writerType == null || _deliveryType == null || _delegateType == null)
                {
                    ChatPlugin.Log.LogWarning("[聊天] NGO 类型未找到");
                    return false;
                }

                var nmType = FindType("Unity.Netcode.NetworkManager");
                var singletonProp = nmType.GetProperty("Singleton",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                _networkManager = singletonProp.GetValue(null);
                if (_networkManager == null) return false;

                var cmmProp = nmType.GetProperty("CustomMessagingManager",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                _messagingManager = cmmProp.GetValue(_networkManager);
                if (_messagingManager == null) return false;

                var cmmType = _messagingManager.GetType();

                // 精确签名匹配
                _sendToAll = cmmType.GetMethod("SendNamedMessageToAll",
                    new[] { typeof(string), _writerType, _deliveryType });
                _sendToServer = cmmType.GetMethod("SendNamedMessage",
                    new[] { typeof(string), typeof(ulong), _writerType, _deliveryType });
                _registerHandler = cmmType.GetMethod("RegisterNamedMessageHandler",
                    new[] { typeof(string), _delegateType });

                if (_sendToAll == null || _sendToServer == null || _registerHandler == null)
                {
                    ChatPlugin.Log.LogWarning("[聊天] NGO 消息 API 未找到");
                    return false;
                }

                // NetworkDelivery.Reliable
                _deliveryReliable = Enum.Parse(_deliveryType, "Reliable");

                RegisterHandler();
                ChatPlugin.Log.LogInfo("[聊天] NGO 联机消息已初始化");
                return true;
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 网络初始化失败: " + e.Message);
                return false;
            }
        }

        private static void DumpNetworkState()
        {
            try
            {
                var nm = GetNetworkManager();
                if (nm == null)
                {
                    ChatPlugin.Log.LogWarning("[聊天] NetworkManager 为 null");
                    return;
                }
                ChatPlugin.Log.LogInfo("[聊天] NetworkManager 类型: " + nm.GetType().FullName);
                var t = nm.GetType();
                foreach (var name in new[] { "IsListening", "IsServer", "IsClient", "IsHost", "IsConnected", "IsConnectedClient" })
                {
                    try
                    {
                        var prop = t.GetProperty(name);
                        if (prop != null)
                        {
                            ChatPlugin.Log.LogInfo("[聊天]   " + name + " = " + prop.GetValue(nm));
                        }
                        else
                        {
                            ChatPlugin.Log.LogInfo("[聊天]   " + name + " = (无此属性)");
                        }
                    }
                    catch (Exception e)
                    {
                        ChatPlugin.Log.LogInfo("[聊天]   " + name + " = 读取异常 " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] DumpNetworkState 异常: " + e.Message);
            }
        }

        private static void HandleMessage(ulong sender, byte[] buf)
        {
            try
            {
                if (buf == null || buf.Length == 0) return;
                int len = buf.Length;
                while (len > 0 && buf[len - 1] == 0) len--;
                if (len == 0) return;
                var msg = System.Text.Encoding.UTF8.GetString(buf, 0, len);
                ChatPlugin.Log.LogInfo("[聊天] 收到消息: sender=" + sender + " 内容=" + msg);
                if (string.IsNullOrEmpty(msg)) return;

                // 统一显示一次（联网时所有端都从这里显示，不再本地另加）
                ChatManager.AddRemoteMessage(msg);

                // 服务器收到远程客户端消息时，转发给其他客户端（含原发送者回显，排除服务器自身避免循环）
                if (IsServer() && sender != GetServerClientId())
                {
                    RelayToClients(buf);
                }
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 处理消息失败: " + e);
            }
        }

        private static void RelayToClients(byte[] buf)
        {
            try
            {
                ulong serverId = GetServerClientId();
                foreach (ulong cid in GetConnectedClientIds())
                {
                    if (cid == serverId) continue;
                    var writer = CreateWriterFromBytes(buf);
                    if (writer == null) continue;
                    try
                    {
                        _sendToServer.Invoke(_messagingManager, new[] { MsgName, cid, writer, _deliveryReliable });
                        ChatPlugin.Log.LogInfo("[聊天] 已转发给客户端 " + cid);
                    }
                    finally
                    {
                        DisposeWriter(writer);
                    }
                }
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 转发消息失败: " + e);
            }
        }

        private static IEnumerable<ulong> GetConnectedClientIds()
        {
            var nm = GetNetworkManager();
            if (nm == null) yield break;
            var t = nm.GetType();
            foreach (var propName in new[] { "ConnectedClientsIds", "ConnectedClientsList", "ConnectedClients" })
            {
                System.Reflection.PropertyInfo prop = null;
                try { prop = t.GetProperty(propName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance); }
                catch (Exception) { }
                if (prop == null) continue;
                object v = null;
                try { v = prop.GetValue(nm); }
                catch (Exception) { }
                if (v == null) continue;

                var en = v as System.Collections.IEnumerable;
                if (en != null)
                {
                    foreach (var o in en)
                    {
                        if (o is ulong u) yield return u;
                    }
                    yield break;
                }
                var keysProp = v.GetType().GetProperty("Keys");
                if (keysProp != null)
                {
                    var keys = keysProp.GetValue(v) as System.Collections.IEnumerable;
                    if (keys != null)
                    {
                        foreach (var o in keys)
                        {
                            if (o is ulong u) yield return u;
                        }
                        yield break;
                    }
                }
            }
        }

        private static void RegisterHandler()
        {
            try
            {
                // 用 Expression 生成签名完全匹配的委托 (ulong, FastBufferReader)->void
                // 体内部直接读字节（struct 原生，不装箱），解码后显示
                var senderParam = System.Linq.Expressions.Expression.Parameter(typeof(ulong), "sender");
                var readerParam = System.Linq.Expressions.Expression.Parameter(_readerType, "reader");

                var readByte = _readerType.GetMethod("ReadByteSafe", new[] { typeof(byte).MakeByRefType() });
                if (readByte == null)
                {
                    ChatPlugin.Log.LogWarning("[聊天] 找不到 ReadByteSafe");
                    return;
                }

                // 生成：byte[] buf = new byte[256]; for(...) buf[i] = reader.ReadByteSafe(out v);
                // 用 Expression.Block 构建循环读 256 字节
                var bufVar = System.Linq.Expressions.Expression.Variable(typeof(byte[]), "buf");
                var newBuf = System.Linq.Expressions.Expression.NewArrayBounds(typeof(byte), System.Linq.Expressions.Expression.Constant(256));
                var assignBuf = System.Linq.Expressions.Expression.Assign(bufVar, newBuf);

                var iVar = System.Linq.Expressions.Expression.Variable(typeof(int), "i");
                var vVar = System.Linq.Expressions.Expression.Variable(typeof(byte), "v");

                // 循环体：v = reader.ReadByteSafe(out v); buf[i] = v; i++
                var callRead = System.Linq.Expressions.Expression.Call(readerParam, readByte, vVar);
                var storeByte = System.Linq.Expressions.Expression.Assign(
                    System.Linq.Expressions.Expression.ArrayAccess(bufVar, iVar), vVar);
                var incI = System.Linq.Expressions.Expression.PostIncrementAssign(iVar);
                var loopBody = System.Linq.Expressions.Expression.Block(callRead, storeByte, incI);

                var breakLabel = System.Linq.Expressions.Expression.Label("brk");
                var loop = System.Linq.Expressions.Expression.Loop(
                    System.Linq.Expressions.Expression.IfThenElse(
                        System.Linq.Expressions.Expression.LessThan(iVar, System.Linq.Expressions.Expression.Constant(256)),
                        loopBody,
                        System.Linq.Expressions.Expression.Break(breakLabel)),
                    breakLabel);

                // 解码 + 显示 + 转发：调静态方法 HandleMessage(sender, buf)
                var handleMethod = typeof(ChatNetwork).GetMethod("HandleMessage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var showCall = System.Linq.Expressions.Expression.Call(handleMethod, senderParam, bufVar);

                var body = System.Linq.Expressions.Expression.Block(
                    new[] { bufVar, iVar, vVar },
                    assignBuf,
                    System.Linq.Expressions.Expression.Assign(iVar, System.Linq.Expressions.Expression.Constant(0)),
                    loop,
                    showCall);

                var lambda = System.Linq.Expressions.Expression.Lambda(_delegateType, body, senderParam, readerParam);
                _handlerDelegate = lambda.Compile();

                _registerHandler.Invoke(_messagingManager, new object[] { MsgName, _handlerDelegate });
                ChatPlugin.Log.LogInfo("[聊天] 消息处理器已注册（Expression 委托）");
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 注册消息处理器失败: " + e);
            }
        }

        private static void OnMessage(ulong senderId, object reader)
        {
            try
            {
                ChatPlugin.Log.LogInfo("[聊天] 收到消息! sender=" + senderId +
                    " readerType=" + (reader != null ? reader.GetType().FullName : "null") +
                    " isValueType=" + (reader != null ? reader.GetType().IsValueType.ToString() : "?"));
                var msg = ReadString(reader);
                ChatPlugin.Log.LogInfo("[聊天] 消息内容: " + msg);
                if (!string.IsNullOrEmpty(msg))
                {
                    ChatManager.AddRemoteMessage(msg);
                }
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 接收消息失败: " + e);
            }
        }

        // 真正的回调（FastBufferReader 是 struct，直接接收）
        private static void OnMessageStruct(ulong senderId, object readerBoxed)
        {
            try
            {
                ChatPlugin.Log.LogInfo("[聊天] 收到消息! sender=" + senderId);
                // readerBoxed 是装箱的 FastBufferReader struct
                var msg = ReadString(readerBoxed);
                ChatPlugin.Log.LogInfo("[聊天] 消息内容: " + msg);
                if (!string.IsNullOrEmpty(msg))
                {
                    ChatManager.AddRemoteMessage(msg);
                }
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] 接收消息失败: " + e);
            }
        }

        // 用 Expression 树预编译"读一个字节"的委托，正确调用 out 参数
        private static Func<object, byte> _byteReader;
        private static System.Reflection.MethodInfo _readByteMethod;

        private static Func<object, byte> BuildByteReader(System.Reflection.MethodInfo m)
        {
            var readerParam = System.Linq.Expressions.Expression.Parameter(typeof(object), "reader");
            var converted = System.Linq.Expressions.Expression.Convert(readerParam, m.DeclaringType);
            var valueVar = System.Linq.Expressions.Expression.Variable(typeof(byte), "v");
            var call = System.Linq.Expressions.Expression.Call(converted, m, valueVar); // out byte
            var block = System.Linq.Expressions.Expression.Block(new[] { valueVar }, call, valueVar);
            return System.Linq.Expressions.Expression.Lambda<Func<object, byte>>(block, readerParam).Compile();
        }

        private static string ReadString(object reader)
        {
            try
            {
                if (_byteReader == null)
                {
                    _readByteMethod = _readerType.GetMethod("ReadByteSafe", new[] { typeof(byte).MakeByRefType() });
                    if (_readByteMethod == null)
                    {
                        ChatPlugin.Log.LogWarning("[聊天] 找不到 ReadByteSafe");
                        return null;
                    }
                    _byteReader = BuildByteReader(_readByteMethod);
                    ChatPlugin.Log.LogInfo("[聊天] 字节读取委托已编译");
                }
                var buf = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    buf[i] = _byteReader(reader);
                }
                int len = 256;
                while (len > 0 && buf[len - 1] == 0) len--;
                if (len == 0) return null;
                return System.Text.Encoding.UTF8.GetString(buf, 0, len);
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] ReadString 异常: " + e);
                return null;
            }
        }

        // 找数组重载 WriteValueSafe<T>(T[] value, ForXXX marker)
        private static System.Reflection.MethodInfo FindArrayWriteMethod(Type t, string name)
        {
            var all = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            System.Reflection.MethodInfo fallback = null;
            foreach (var m in all)
            {
                if (m.Name != name) continue;
                if (!m.IsGenericMethodDefinition) continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;
                if (!(ps[0].ParameterType.IsArray &&
                      ps[0].ParameterType.GetElementType().IsGenericParameter)) continue;

                // 检查泛型约束
                var gs = m.GetGenericArguments();
                if (gs.Length == 0) continue;
                var cons = gs[0].GetGenericParameterConstraints();
                var conNames = Array.ConvertAll(cons, c => c.Name);
                var hasValueType = Array.Exists(cons, c => c == typeof(ValueType));
                var hasNetworkSerializable = Array.Exists(conNames, n => n == "INetworkSerializable");

                ChatPlugin.Log.LogInfo("[聊天] 数组重载约束: " + name + " -> " + string.Join("|", conNames));

                // 优先选 ValueType 约束（byte 满足），避免 INetworkSerializable
                if (hasValueType) return m;
                if (!hasNetworkSerializable && fallback == null) fallback = m;
            }
            return fallback;
        }

        // 找 WriteValueSafe<T>(T value) 单值重载（约束 ValueType，int 满足）
        private static System.Reflection.MethodInfo FindValueTypeWriteMethod(Type t, string name)
        {
            foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (m.Name != name) continue;
                if (!m.IsGenericMethodDefinition) continue;
                if (m.GetParameters().Length != 2) continue; // (T value, marker)
                var gargs = m.GetGenericArguments();
                if (gargs.Length != 1) continue;
                var cons = gargs[0].GetGenericParameterConstraints();
                bool hasValueType = false;
                foreach (var c in cons)
                {
                    if (c == typeof(ValueType)) { hasValueType = true; break; }
                }
                if (!hasValueType) continue;
                return m;
            }
            return null;
        }

        private static object CreateWriter(string message)
        {
            if (message == null) return null;
            var bytes = System.Text.Encoding.UTF8.GetBytes(message);
            if (bytes.Length > 256) return null;
            return CreateWriterFromBytes(bytes);
        }

        private static object CreateWriterFromBytes(byte[] bytes)
        {
            try
            {
                if (bytes == null || bytes.Length > 256) return null;
                var allocType = FindType("Unity.Collections.Allocator");
                var temp = Enum.Parse(allocType, "Temp");
                var writer = Activator.CreateInstance(_writerType, new object[] { 512, temp, 512 });

                // 逐字节写（WriteByteSafe(Byte) 委托，与读端 ReadByteSafe 对称）
                var wbyte = _writerType.GetMethod("WriteByteSafe", new[] { typeof(byte) });
                if (wbyte == null)
                {
                    ChatPlugin.Log.LogWarning("[聊天] 找不到 WriteByteSafe");
                    return null;
                }
                var wdel = (WriteByteSafeDel)Delegate.CreateDelegate(typeof(WriteByteSafeDel), writer, wbyte);
                for (int i = 0; i < 256; i++)
                {
                    byte b = i < bytes.Length ? bytes[i] : (byte)0;
                    wdel(b);
                }
                return writer;
            }
            catch (Exception e)
            {
                ChatPlugin.Log.LogWarning("[聊天] CreateWriter 失败: " + e);
                return null;
            }
        }

        private static void DisposeWriter(object writer)
        {
            try
            {
                var d = writer.GetType().GetMethod("Dispose");
                if (d != null) d.Invoke(writer, null);
            }
            catch (Exception) { }
        }

        private static bool IsConnected()
        {
            try
            {
                var nm = GetNetworkManager();
                if (nm == null) return false;
                var t = nm.GetType();
                // 任一为真即认为已联网
                foreach (var name in new[] { "IsListening", "IsServer", "IsClient", "IsConnectedClient", "IsHost" })
                {
                    try
                    {
                        var prop = t.GetProperty(name);
                        if (prop == null) continue;
                        var v = prop.GetValue(nm);
                        if (v is bool b && b)
                            return true;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static bool IsServer()
        {
            try
            {
                var nm = GetNetworkManager();
                if (nm == null) return false;
                var prop = nm.GetType().GetProperty("IsServer");
                var v = prop.GetValue(nm);
                return v is bool b && b;
            }
            catch (Exception) { }
            return false;
        }

        private static object GetNetworkManager()
        {
            try
            {
                if (_networkManager != null) return _networkManager;
                var nmType = FindType("Unity.Netcode.NetworkManager");
                if (nmType == null) return null;
                var singletonProp = nmType.GetProperty("Singleton",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (singletonProp == null) return null;
                _networkManager = singletonProp.GetValue(null);
                return _networkManager;
            }
            catch (Exception) { }
            return _networkManager;
        }

        private static ulong GetServerClientId()
        {
            try
            {
                var prop = _networkManager.GetType().GetProperty("ServerClientId");
                var v = prop.GetValue(_networkManager);
                if (v is ulong u) return u;
            }
            catch (Exception) { }
            return 0;
        }

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
