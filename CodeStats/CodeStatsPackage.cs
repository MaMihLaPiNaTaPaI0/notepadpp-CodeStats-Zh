using Kbg.NppPluginNET.PluginInfrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Task = System.Threading.Tasks.Task;

namespace CodeStats
{
    class CodeStatsPackage
    {
        #region Properties
        internal const string PluginName = Constants.PluginName;

        static int idMyDlg = -1;
        static Bitmap tbBmp = Properties.Resources.CodeStats;

        static ConfigFile _CodeStatsConfigFile;
        public static CodeStats.Forms.SettingsForm _settingsForm;
        public static CodeStats.Forms.ApiKeyForm _apikeyForm; 
        static string _lastStatusBarDocTypeText;
        static DateTime _lastPulse = DateTime.Now;
        static DateTime _lastActivity = DateTime.Now;
        static int pulseFrequency = 10000; // ms

        public static bool Debug;
        public static string ApiKey;
        public static string ApiUrl;
        public static string Proxy;
        public static bool proxyChangePending = false;
        public static bool Stats;
        public static string Guid;
        public static List<Constants.DetectionType> DetectionOrder;

        public static bool _reportedStats = false;
        public static bool _hasAlreadyShownInvalidApiTokenMessage = false;

        public static string currentLangDesc = ""; 
        public static string currentLanguage = ""; 
        public static int currentCount = 0;
        public static Pulse currentPulse;

        static bool nppStarted = false;

        // 记录上一次文档的总长度
        private static int _lastDocLength = -1;

        static Dictionary<string, string> extensionMapping;
        static Dictionary<string, string> customExtensionMapping;

        private static ConcurrentQueue<Pulse> pulseQueue = new ConcurrentQueue<Pulse>();
        private static System.Timers.Timer timer = new System.Timers.Timer();

        public static Task pulseProcessor { get; set; }
        private static CancellationTokenSource pulseProcessor_tokensource;
        private static HttpClient pulseProcessor_client;
        private static HttpClientHandler pulseProcessor_httpClientHandler;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, ref int lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        #endregion

        internal static void CommandMenuInit()
        {
            PluginBase.SetCommand(0, "Code::Stats settings", SettingsPopup, new ShortcutKey(false, false, false, Keys.None));
            idMyDlg = 0;

            Task.Run(() =>
            {
                InitializeAsync();
            });
        }

        private static void InitializeAsync()
        {
            if (System.Net.ServicePointManager.SecurityProtocol != 0)
            {
                System.Net.ServicePointManager.SecurityProtocol &= ~SecurityProtocolType.Ssl3;
                System.Net.ServicePointManager.SecurityProtocol &= ~SecurityProtocolType.Tls;
                System.Net.ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288 | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            }

            try { Logger.Delete(); } catch { }

            Logger.Info(string.Format("Initializing Code::Stats v{0}", Constants.PluginVersion));

            try
            {
                currentPulse = new Pulse();
                _CodeStatsConfigFile = new ConfigFile();
                GetSettings(true);

                LoadExtensionMapping();
                LoadCustomExtensionMapping();

                if (string.IsNullOrEmpty(ApiKey))
                {
                    Stats = false;
                    PromptApiKey();
                }

                pulseProcessor_tokensource = new CancellationTokenSource();
                pulseProcessor_httpClientHandler = new HttpClientHandler();
                pulseProcessor_httpClientHandler.Proxy = GetProxy();
                proxyChangePending = false;
                pulseProcessor_client = new HttpClient(pulseProcessor_httpClientHandler);
                timer.Interval = pulseFrequency;
                timer.Elapsed += ProcessPulses;
                timer.AutoReset = false;

                if (Stats) { ReportStats(); }

                Task.Run(() =>
                {
                    try
                    {
                        var path = ConfigFile.GetUnsavedPulsesFilePath();
                        if (File.Exists(path))
                        {
                            var json = File.ReadAllText(path);
                            var serializer = new JavaScriptSerializer();
                            var pulses = serializer.Deserialize<List<Pulse>>(json);
                            foreach (var pulse in pulses) { pulseQueue.Enqueue(pulse); }
                            UpdateStatusbar();
                            File.Delete(path);
                        }
                    }
                    catch { }
                });
            }
            catch (Exception ex) { Logger.Error("Error Initializing Code::Stats", ex); }
        }

        private static void LoadExtensionMapping()
        {
            try
            {
                Assembly _assembly = Assembly.GetExecutingAssembly();
                Stream _stream = _assembly.GetManifestResourceStream("CodeStats.Resources.extension_mapping.json");
                StreamReader _textStreamReader = new StreamReader(_stream);
                string extensionMappingJson = _textStreamReader.ReadToEnd();
                var serializer = new JavaScriptSerializer();
                extensionMapping = serializer.Deserialize<Dictionary<string, string>>(extensionMappingJson);

                Task.Run(() =>
                {
                    string extensionMappingJsonNew = "";
                    var serializer2 = new JavaScriptSerializer();
                    try
                    {
                        var client = new WebClient { Proxy = CodeStatsPackage.GetProxy() };
                        client.Headers[HttpRequestHeader.UserAgent] = Constants.PluginUserAgent;
                        try { extensionMappingJsonNew = client.DownloadString("https://raw.githubusercontent.com/p0358/notepadpp-CodeStats/master/CodeStats/Resources/extension_mapping.json").Trim(); }
                        catch { }
                    }
                    catch { }

                    if (!String.IsNullOrWhiteSpace(extensionMappingJsonNew) && extensionMappingJsonNew != extensionMappingJson)
                    {
                        extensionMapping = serializer2.Deserialize<Dictionary<string, string>>(extensionMappingJson);
                    }
                });
            }
            catch { }
        }

        private static void LoadCustomExtensionMapping()
        {
            try
            {
                StringBuilder sbConfigFilePath = new StringBuilder(Win32.MAX_PATH);
                Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETPLUGINSCONFIGDIR, Win32.MAX_PATH, sbConfigFilePath);
                string configFilePath = sbConfigFilePath.ToString();
                string customExtensionMappingFilePath = ConfigFile.GetCustomExtensionMappingFilePath();

                StreamReader _textStreamReader = File.OpenText(customExtensionMappingFilePath);
                string customExtensionMappingJson = _textStreamReader.ReadToEnd();
                var serializer = new JavaScriptSerializer();
                customExtensionMapping = serializer.Deserialize<Dictionary<string, string>>(customExtensionMappingJson);
            }
            catch { }
        }

        internal static void SetToolBarIcon()
        {
            toolbarIcons tbIcons = new toolbarIcons();
            tbIcons.hToolbarBmp = tbBmp.GetHbitmap();
            IntPtr pTbIcons = Marshal.AllocHGlobal(Marshal.SizeOf(tbIcons));
            Marshal.StructureToPtr(tbIcons, pTbIcons, false);
            Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_ADDTOOLBARICON, PluginBase._funcItems.Items[idMyDlg]._cmdID, pTbIcons);
            Marshal.FreeHGlobal(pTbIcons);
        }

        internal static void SetStatusBarDocType(string str)
        {
            Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_SETSTATUSBAR, (IntPtr)NppMsg.STATUSBAR_DOC_TYPE, str);
        }

        public static void OnNotification(ScNotification notification)
        {
            if (notification.Header.Code == (uint)NppMsg.NPPN_LANGCHANGED)
            {
                UpdateStatusbar();
            }
            if (notification.Header.Code == (uint)NppMsg.NPPN_READY)
            {
                nppStarted = true;
                UpdateStatusbar();

                int which = 0;
                SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTSCINTILLA, IntPtr.Zero, ref which);
                IntPtr curScintilla = (which == 0) ? PluginBase.nppData._scintillaMainHandle : PluginBase.nppData._scintillaSecondHandle;
                _lastDocLength = SendMessage(curScintilla, 2006, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }

            if (notification.Header.Code == (uint)NppMsg.NPPN_BUFFERACTIVATED)
            {
                int which = 0;
                SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTSCINTILLA, IntPtr.Zero, ref which);
                IntPtr curScintilla = (which == 0) ? PluginBase.nppData._scintillaMainHandle : PluginBase.nppData._scintillaSecondHandle;
                _lastDocLength = SendMessage(curScintilla, 2006, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }

            // 【核心霸道修改】：彻底废掉原有的按单键加 1 分的逻辑
            // if (notification.Header.Code == (uint)SciMsg.SCN_CHARADDED) { HandleActivity(); }

            if (nppStarted && notification.Header.Code == (uint)SciMsg.SCN_MODIFIED)
            {
                bool isInsert = (notification.ModificationType & (int)SciMsg.SC_MOD_INSERTTEXT) != 0;

                int which = 0;
                SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTSCINTILLA, IntPtr.Zero, ref which);
                IntPtr curScintilla = (which == 0) ? PluginBase.nppData._scintillaMainHandle : PluginBase.nppData._scintillaSecondHandle;
                
                int currentLength = SendMessage(curScintilla, 2006, IntPtr.Zero, IntPtr.Zero).ToInt32();

                if (isInsert)
                {
                    if (_lastDocLength == -1) _lastDocLength = currentLength; // 安全初始化

                    if (currentLength > _lastDocLength)
                    {
                        int delta = currentLength - _lastDocLength;
                        
                        // 1 个汉字=3 字节，除以 3 算出字符数
                        int chars = (int)Math.Ceiling((double)delta / 3.0);
                        
                        // 【核心需求】：1 个字符 = 10 点经验
                        int xpToAdd = chars * 10;
                        
                        // 单次最高 300（防止大段粘贴刷分）
                        if (xpToAdd > 300) xpToAdd = 300;

                        HandleActivity(xpToAdd);
                    }
                }
                _lastDocLength = currentLength;
            }

            // 删除操作（退格键）保留加 1 分的逻辑
            int SC_PERFORMED_USER_AND_SC_MOD_DELETETEXT = (int)SciMsg.SC_PERFORMED_USER | (int)SciMsg.SC_MOD_DELETETEXT;
            if (nppStarted && notification.Header.Code == (uint)SciMsg.SCN_MODIFIED && ((notification.ModificationType & SC_PERFORMED_USER_AND_SC_MOD_DELETETEXT) == SC_PERFORMED_USER_AND_SC_MOD_DELETETEXT))
            {
                HandleActivity(1);
            }
        }

        public static void UpdateStatusbar()
        {
            currentLangDesc = GetCurrentLangDesc();
            string append = @"";
            if (!String.IsNullOrWhiteSpace(currentLangDesc)) append = @" | " + currentLangDesc;
            string newStatusBarDocTypeText = @"✎C::S " + currentCount.ToString() + append;
            if (_lastStatusBarDocTypeText != newStatusBarDocTypeText)
            {
                SetStatusBarDocType(newStatusBarDocTypeText);
            }
            _lastStatusBarDocTypeText = newStatusBarDocTypeText;
        }

        public static void HandleActivity(int xpAmount = 1)
        {
            if (!nppStarted) return;

            currentCount += xpAmount;

            UpdateStatusbar();

            currentLanguage = GetCurrentLanguage();
            currentPulse.addXpForLanguage(currentLanguage, xpAmount);

            _lastActivity = DateTime.Now;
            timer.Start();
        }

        private static void FlushCurrentPulseIfNeeded()
        {
            if (pulseQueue != null && currentPulse != null && !currentPulse.isEmpty())
            {
                pulseQueue.Enqueue(currentPulse);
                currentPulse = new Pulse();
                currentCount = 0;
            }
        }

        private static void ProcessPulses(object sender, ElapsedEventArgs e)
        {
            Task.Run(() =>
            {
                try
                {
                    if (pulseQueue != null && ((currentPulse != null && !currentPulse.isEmpty()) || !pulseQueue.IsEmpty))
                    {
                        if (!EnoughTimePassed(DateTime.Now))
                        {
                            timer.Start();
                            return;
                        }
                        FlushCurrentPulseIfNeeded();
                        if (pulseProcessor == null || pulseProcessor.IsCompleted || pulseProcessor.IsFaulted) 
                        {
                            try { pulseProcessor = ProcessPendingPulses(pulseProcessor_tokensource); }
                            catch (OperationCanceledException) { }
                        }
                    }
                    UpdateStatusbar();
                }
                catch (Exception ex) { Logger.Error("Error processing pulses", ex); }
            });
        }

        private static Task ProcessPendingPulses(CancellationTokenSource tokenSource)
        {
            return Task.Run(async () =>
            {
                if (pulseQueue != null && ((currentPulse != null && !currentPulse.isEmpty()) || !pulseQueue.IsEmpty) && EnoughTimePassed(DateTime.Now))
                {
                    if (String.IsNullOrWhiteSpace(ApiKey)) return;

                    if (proxyChangePending)
                    {
                        pulseProcessor_httpClientHandler = new HttpClientHandler();
                        pulseProcessor_httpClientHandler.Proxy = GetProxy();
                        pulseProcessor_client = new HttpClient(pulseProcessor_httpClientHandler);
                        proxyChangePending = false;
                    }
                    var jsonSerializer = new JavaScriptSerializer();

                    string URL = String.IsNullOrWhiteSpace(ApiUrl) ? Constants.ApiEndpoint : ApiUrl;
                    if (!URL.EndsWith("my/pulses")) URL += "my/pulses";

                    Pulse result;
                    while (pulseQueue.TryDequeue(out result))
                    {
                        if (!result.isEmpty())
                        {
                            bool error = false;
                            HttpResponseMessage response = null;
                            try
                            {
                                string json;
                                var httpRequestMessage = new HttpRequestMessage
                                {
                                    Method = HttpMethod.Post,
                                    RequestUri = new Uri(URL),
                                    Headers = { { "User-Agent", Constants.PluginUserAgent }, { "Accept", "*/*" }, { "X-API-Token", ApiKey } },
                                    Content = new StringContent(json = jsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
                                };

                                response = await pulseProcessor_client.SendAsync(httpRequestMessage, tokenSource.Token);
                                response.EnsureSuccessStatusCode();
                                _lastPulse = DateTime.Now;
                            }
                            catch { error = true; }

                            if (error) { pulseQueue.Enqueue(result); return; }
                        }
                        tokenSource.Token.ThrowIfCancellationRequested();
                    }
                }
            }, tokenSource.Token);
        }

        public static string GetCurrentFile()
        {
            var currentFile = new StringBuilder(Win32.MAX_PATH);
            return (int)Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETFULLCURRENTPATH, 0, currentFile) != -1 ? currentFile.ToString() : null;
        }

        public static LangType GetCurrentLangType()
        {
            LangType docType = LangType.L_TEXT;
            Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTLANGTYPE, 0, ref docType);
            return docType;
        }

        public static string GetCurrentLangName() { return GetLangName(GetCurrentLangType()); }

        public static string GetLangName(LangType langType) 
        {
            var languageName = new StringBuilder(Win32.MAX_PATH);
            return (int)Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETLANGUAGENAME, (IntPtr)langType, languageName) != -1 ? languageName.ToString() : null;
        }

        public static string GetCurrentLangDesc() { return GetLangDesc(GetCurrentLangType()); }

        public static string GetLangDesc(LangType langType) 
        {
            var languageDesc = new StringBuilder(Win32.MAX_PATH);
            return (int)Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETLANGUAGEDESC, (IntPtr)langType, languageDesc) != -1 ? languageDesc.ToString() : null;
        }

        public static string GetCurrentLanguage() 
        {
            LangType langType = GetCurrentLangType();
            string currentFile = GetCurrentFile();
            string extension = currentFile != null ? Path.GetExtension(currentFile).TrimStart('.') : "";

            bool found = false;
            string language = "";

            foreach (var detectionMethod in DetectionOrder)
            {
                switch (detectionMethod)
                {
                    case Constants.DetectionType.CUSTOM_MAPPING:
                        if (!String.IsNullOrWhiteSpace(extension))
                        {
                            try { language = customExtensionMapping[extension]; found = true; } catch { found = false; }
                        }
                        break;
                    case Constants.DetectionType.EXTENSION_MAPPING:
                        if (!String.IsNullOrWhiteSpace(extension))
                        {
                            try { language = extensionMapping[extension]; found = true; } catch { found = false; }
                        }
                        break;
                    case Constants.DetectionType.LEXER_LANGUAGE:
                        found = true; 
                        switch (langType)
                        {
                            case LangType.L_C: language = "C"; break;
                            case LangType.L_CPP: language = "C++"; break;
                            case LangType.L_CS: language = "C#"; break;
                            case LangType.L_CSS: language = "CSS"; break;
                            case LangType.L_HTML: language = "HTML"; break;
                            case LangType.L_JAVA: language = "Java"; break;
                            case LangType.L_JAVASCRIPT: language = "JavaScript"; break;
                            case LangType.L_JSON: language = "JSON"; break;
                            case LangType.L_PYTHON: language = "Python"; break;
                            case LangType.L_SQL: language = "SQL"; break;
                            case LangType.L_XML: language = "XML"; break;
                            case LangType.L_YAML: language = "YAML"; break;
                            case LangType.L_TEXT: default: found = false; break;
                        }
                        break;
                }
                if (found) break;
            }

            if (!found || String.IsNullOrWhiteSpace(language)) language = "Plain text";
            return language;
        }

        public static bool EnoughTimePassed(DateTime now) { return _lastActivity < now.AddMilliseconds(-1 * pulseFrequency) && _lastPulse < now.AddMilliseconds(-1 * pulseFrequency); }

        public static void GetSettings(bool skipRead = false)
        {
            if (!skipRead) _CodeStatsConfigFile.Read();
            ApiKey = _CodeStatsConfigFile.ApiKey;
            ApiUrl = _CodeStatsConfigFile.ApiUrl;
            Debug = _CodeStatsConfigFile.Debug;
            Proxy = _CodeStatsConfigFile.Proxy;
            Stats = _CodeStatsConfigFile.Stats;
            CodeStatsPackage.Guid = _CodeStatsConfigFile.Guid;
            DetectionOrder = _CodeStatsConfigFile.DetectionOrder;
            proxyChangePending = true;
        }

        private static void PromptApiKey()
        {
            _apikeyForm = new CodeStats.Forms.ApiKeyForm();
            _apikeyForm.ShowDialog();
        }

        private static void SettingsPopup()
        {
            if (_settingsForm == null)
            {
                _settingsForm = new CodeStats.Forms.SettingsForm();
                _settingsForm.OnConfigSaved += SettingsFormOnConfigSaved;
            }
            _settingsForm.Visible = false; 
            _settingsForm.ShowDialog();
        }

        private static void SettingsFormOnConfigSaved(object sender, EventArgs eventArgs)
        {
            GetSettings();
            LoadCustomExtensionMapping();
            _settingsForm.OnConfigSaved -= SettingsFormOnConfigSaved;
            _settingsForm.Dispose();
            _settingsForm = null;
        }

        public static void ReportStats()
        {
            Task.Run(() =>
            {
                try
                {
                    var client = new WebClient { Proxy = CodeStatsPackage.GetProxy() };
                    client.Headers[HttpRequestHeader.UserAgent] = Constants.PluginUserAgent;
                    string result = client.DownloadString("https://p0358.net/codestats/report.php?pluginver=" + Constants.PluginVersion
                        + "&cid=" + CodeStatsPackage.Guid + "&editorname=" + Constants.EditorName + "&editorver=" + Constants.EditorVersion
                        + "&is64process=" + ProcessorArchitectureHelper.Is64BitProcess.ToString().ToLowerInvariant() + "&is64sys=" + ProcessorArchitectureHelper.Is64BitOperatingSystem.ToString().ToLowerInvariant()
                        + "&osverstr=" + Constants.OSVersionString + "&osbuild=" + Constants.OSVersionBuild
                    );
                    if (result.Contains("ok")) _reportedStats = true;
                }
                finally { }
            });
        }

        public static WebProxy GetProxy()
        {
            WebProxy proxy = null;
            if (String.IsNullOrWhiteSpace(Proxy)) return proxy;

            try
            {
                var proxyStr = Proxy;
                var regProxyWithAuth = new Regex(@"\s*(https?:\/\/)?([^\s:]+):([^\s:]+)@([^\s:]+):(\d+)\s*");
                var match = regProxyWithAuth.Match(proxyStr);

                if (match.Success)
                {
                    var credentials = new NetworkCredential(match.Groups[2].Value, match.Groups[3].Value);
                    proxy = new WebProxy($"{match.Groups[4].Value}:{match.Groups[5].Value}", true, null, credentials);
                    return proxy;
                }

                var regProxy = new Regex(@"\s*(https?:\/\/)?([^\s@]+):(\d+)\s*");
                match = regProxy.Match(proxyStr);

                if (match.Success) return new WebProxy(match.Groups[2].Value, int.Parse(match.Groups[3].Value));
            }
            catch (Exception ex) { Logger.Error("Exception while parsing the proxy string", ex); }
            return proxy;
        }

        internal static void RecursiveDelete(string folder)
        {
            try { Directory.Delete(folder, true); } catch { }
            try { File.Delete(folder); } catch { }
        }

        internal static class CoreAssembly
        {
            public static readonly Assembly Reference = typeof(CoreAssembly).Assembly;
            public static readonly Version Version = Reference.GetName().Version;
            public static readonly string Location = Reference.Location;
        }

        internal static void PluginCleanUp()
        {
            nppStarted = false;
            try
            {
                FlushCurrentPulseIfNeeded();
                pulseProcessor_tokensource.Cancel();
                if (timer != null) { timer.Stop(); timer.Elapsed -= ProcessPulses; timer.Dispose(); timer = null; }
                try { if (pulseProcessor != null) pulseProcessor.Wait(); } catch { }

                var jsonSerializer = new JavaScriptSerializer();
                var unsavedPulses = new List<Pulse>();
                Pulse result;
                while (pulseQueue.TryDequeue(out result)) { if (!result.isEmpty()) unsavedPulses.Add(result); }
                
                if (unsavedPulses.Count > 0) File.WriteAllText(ConfigFile.GetUnsavedPulsesFilePath(), jsonSerializer.Serialize(unsavedPulses));
                Logger.FlushEverything();
            }
            catch (Exception ex) { Logger.Error("Exception while performing plugin shutdown cleanup", ex); Logger.FlushEverything(); }
        }
    }
}
