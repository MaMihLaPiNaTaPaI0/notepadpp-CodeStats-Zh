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
        public static CodeStats.Forms.ApiKeyForm _apikeyForm; // should be null if not needed
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

        // 记录上一次文档的总长度，用于计算差分（核心修复）
        private static int _lastDocLength = -1;

        static Dictionary<string, string> extensionMapping;
        static Dictionary<string, string> customExtensionMapping;

        private static ConcurrentQueue<Pulse> pulseQueue = new ConcurrentQueue<Pulse>();
        private static System.Timers.Timer timer = new System.Timers.Timer();

        public static Task pulseProcessor { get; set; }
        private static CancellationTokenSource pulseProcessor_tokensource;
        private static HttpClient pulseProcessor_client;
        private static HttpClientHandler pulseProcessor_httpClientHandler;

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

            try
            {
                Logger.Delete();
            }
            catch { }

            Logger.Info(string.Format("Initializing Code::Stats v{0}", Constants.PluginVersion));

            try
            {
                currentPulse = new Pulse();
                _CodeStatsConfigFile = new ConfigFile();
                GetSettings(true);
                Logger.Debug("Loaded config");

                LoadExtensionMapping();
                LoadCustomExtensionMapping();

                Task.Run(() =>
                {
                    try
                    {
                        string latestVersionString = Constants.LatestPluginVersion();
                        Logger.Debug("Latest version of the plugin online is: " + latestVersionString);
                        Version latest = new Version(latestVersionString);
                        Version current = new Version(Constants.PluginVersion);
                        if (current.CompareTo(latest) < 0 && !String.IsNullOrWhiteSpace(latestVersionString))
                        {
                            Logger.Info("Displaying update available notice");
                            MessageBox.Show("There is Code::Stats plugin update available!\nDownload it from Plugins Admin or GitHub. If the update is not available in Plugins Admin, you may need to update your Notepad++ version first.\nYour version: " + Constants.PluginVersion + "\nLatest: " + latestVersionString, "Code::Stats");
                        }
                    }
                    catch { }
                });

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

                if (Stats)
                {
                    ReportStats();
                }

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
                            foreach (var pulse in pulses)
                            {
                                Logger.Info("Restoring dumped pulse: " + serializer.Serialize(pulse));
                                pulseQueue.Enqueue(pulse);
                            }
                            UpdateStatusbar();
                            File.Delete(path);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Error trying to restore dumped pulses from file!", ex);
                    }
                });

                Logger.Info(string.Format("Finished initializing Code::Stats v{0}", Constants.PluginVersion));
            }
            catch (Exception ex)
            {
                Logger.Error("Error Initializing Code::Stats", ex);
            }
        }

        private static void LoadExtensionMapping()
        {
            try
            {
                Assembly _assembly;
                StreamReader _textStreamReader;
                Stream _stream;
                string extensionMappingJson;
                var serializer = new JavaScriptSerializer();

                _assembly = Assembly.GetExecutingAssembly();
                _stream = _assembly.GetManifestResourceStream("CodeStats.Resources.extension_mapping.json");
                _textStreamReader = new StreamReader(_stream);
                extensionMappingJson = _textStreamReader.ReadToEnd();
                extensionMapping = serializer.Deserialize<Dictionary<string, string>>(extensionMappingJson);

                Logger.Debug("Loaded local precompiled extension mapping");

                Task.Run(() =>
                {
                    string extensionMappingJsonNew = "";
                    var serializer2 = new JavaScriptSerializer();
                    try
                    {
                        var client = new WebClient { Proxy = CodeStatsPackage.GetProxy() };
                        client.Headers[HttpRequestHeader.UserAgent] = Constants.PluginUserAgent;

                        try
                        {
                            extensionMappingJsonNew = client.DownloadString("https://raw.githubusercontent.com/p0358/notepadpp-CodeStats/master/CodeStats/Resources/extension_mapping.json").Trim();
                            if (!extensionMappingJsonNew.StartsWith("{") || !extensionMappingJson.EndsWith("}"))
                            {
                                extensionMappingJsonNew = string.Empty;
                                Logger.Error("Invalid response when trying to download latest extension mappings, using local ones instead");
                            }
                        }
                        catch (Exception ex)
                        {
                            extensionMappingJson = string.Empty;
                            Logger.Error("Exception when trying to download latest extension mappings, using local ones instead", ex);
                        }
                    }
                    catch
                    {
                        extensionMappingJson = string.Empty;
                    }

                    if (!String.IsNullOrWhiteSpace(extensionMappingJsonNew) && extensionMappingJsonNew != extensionMappingJson)
                    {
                        extensionMapping = serializer2.Deserialize<Dictionary<string, string>>(extensionMappingJson);
                        Logger.Debug("Loaded latest extension mapping JSON");
                    }
                    else Logger.Debug("There are no updates to extension mapping JSON");
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Error loading extension mappings!", ex);
            }
        }

        private static void LoadCustomExtensionMapping()
        {
            try
            {
                StringBuilder sbConfigFilePath = new StringBuilder(Win32.MAX_PATH);
                Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETPLUGINSCONFIGDIR, Win32.MAX_PATH, sbConfigFilePath);
                string configFilePath = sbConfigFilePath.ToString();

                string customExtensionMappingFilePath = ConfigFile.GetCustomExtensionMappingFilePath();

                StreamReader _textStreamReader;
                string customExtensionMappingJson;
                var serializer = new JavaScriptSerializer();

                _textStreamReader = File.OpenText(customExtensionMappingFilePath);
                customExtensionMappingJson = _textStreamReader.ReadToEnd();
                customExtensionMapping = serializer.Deserialize<Dictionary<string, string>>(customExtensionMappingJson);

                Logger.Debug("Loaded custom extension mapping");
            }
            catch (FileNotFoundException)
            {
                Logger.Debug("Custom extension mapping JSON file does not exist");
            }
            catch (Exception ex)
            {
                Logger.Error("Error loading custom extension mappings!", ex);
            }
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

                // 初始化长度基准
                int which = -1;
                Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTSCINTILLA, 0, ref which);
                IntPtr curScintilla = (which == 0) ? PluginBase.nppData._scintillaMainHandle : PluginBase.nppData._scintillaSecondHandle;
                // 使用 IntPtr.Zero 修复编译报错
                _lastDocLength = Win32.SendMessage(curScintilla, 2006 /* SCI_GETLENGTH */, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }

            // 切换标签时重新获取基准
            if (notification.Header.Code == (uint)NppMsg.NPPN_BUFFERACTIVATED)
            {
                int which = -1;
                Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTSCINTILLA, 0, ref which);
                IntPtr curScintilla = (which == 0) ? PluginBase.nppData._scintillaMainHandle : PluginBase.nppData._scintillaSecondHandle;
                _lastDocLength = Win32.SendMessage(curScintilla, 2006 /* SCI_GETLENGTH */, IntPtr.Zero, IntPtr.Zero).ToInt32();
            }

            if (notification.Header.Code == (uint)SciMsg.SCN_CHARADDED)
            {
                HandleActivity();
            }

            // ================== 【新增：支持中文输入统计（SCI_GETLENGTH 差分法）】 ==================
            if (nppStarted && notification.Header.Code == (uint)SciMsg.SCN_MODIFIED)
            {
                bool isInsert = (notification.ModificationType & (int)SciMsg.SC_MOD_INSERTTEXT) != 0;
                bool isUser = (notification.ModificationType & (int)SciMsg.SC_PERFORMED_USER) != 0;

                int which = -1;
                Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTSCINTILLA, 0, ref which);
                IntPtr curScintilla = (which == 0) ? PluginBase.nppData._scintillaMainHandle : PluginBase.nppData._scintillaSecondHandle;
                
                // 修复编译报错的写法
                int currentLength = Win32.SendMessage(curScintilla, 2006 /* SCI_GETLENGTH */, IntPtr.Zero, IntPtr.Zero).ToInt32();

                if (isInsert && isUser)
                {
                    if (_lastDocLength != -1 && currentLength > _lastDocLength)
                    {
                        int delta = currentLength - _lastDocLength;
                        // UTF-8 编码，中文3字节，英文1字节，向上取整保证都算1点
                        int xpToAdd = (int)Math.Ceiling((double)delta / 3.0);
                        
                        if (xpToAdd > 15) xpToAdd = 15; // 单次上限 15

                        HandleActivity(xpToAdd);
                    }
                }
                _lastDocLength = currentLength;
            }
            // ===========================================================================================

            int SC_PERFORMED_USER_AND_SC_MOD_DELETETEXT = (int)SciMsg.SC_PERFORMED_USER | (int)SciMsg.SC_MOD_DELETETEXT;
            if (nppStarted && notification.Header.Code == (uint)SciMsg.SCN_MODIFIED && ((notification.ModificationType & SC_PERFORMED_USER_AND_SC_MOD_DELETETEXT) == SC_PERFORMED_USER_AND_SC_MOD_DELETETEXT))
            {
                HandleActivity();
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
                    if (String.IsNullOrWhiteSpace(ApiKey))
                    {
                        Logger.Warning("No API token - cannot pulse!");
                        return;
                    }

                    if (proxyChangePending)
                    {
                        pulseProcessor_httpClientHandler = new HttpClientHandler();
                        pulseProcessor_httpClientHandler.Proxy = GetProxy();
                        pulseProcessor_client = new HttpClient(pulseProcessor_httpClientHandler);
                        proxyChangePending = false;
                    }
                    var jsonSerializer = new JavaScriptSerializer();

                    string URL;
                    bool usesCustomEndpoint = false;
                    if (String.IsNullOrWhiteSpace(ApiUrl)) { URL = Constants.ApiEndpoint; }
                    else { URL = ApiUrl; usesCustomEndpoint = true; }
                    
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
                                    Headers = {
                                        { "User-Agent", Constants.PluginUserAgent },
                                        { "Accept", "*/*" },
                                        { "X-API-Token", ApiKey }
                                    },
                                    Content = new StringContent(json = jsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
                                };

                                response = await pulseProcessor_client.SendAsync(httpRequestMessage, tokenSource.Token);
                                response.EnsureSuccessStatusCode();
                                string JsonResult = await response.Content.ReadAsStringAsync();
                                _lastPulse = DateTime.Now;
                                if (!JsonResult.Contains(@"""ok""") && !JsonResult.Contains(@"success"))
                                {
                                    error = true;
                                }
                            }
                            catch (TaskCanceledException)
                            {
                                pulseQueue.Enqueue(result); 
                                return;
                            }
                            catch (HttpRequestException ex)
                            {
                                error = true;
                                if (response != null && (int)response.StatusCode == 403)
                                {
                                    if (!_hasAlreadyShownInvalidApiTokenMessage)
                                    {
                                        _hasAlreadyShownInvalidApiTokenMessage = true;
                                        MessageBox.Show("Could not pulse. Please make sure you entered a valid API token.", "Code::Stats – error 403", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                        PromptApiKey();
                                    }
                                }
                            }
                            catch (Exception ex) { error = true; }

                            if (error)
                            {
                                pulseQueue.Enqueue(result); 
                                return;
                            }
                        }
                        tokenSource.Token.ThrowIfCancellationRequested();
                    }
                }
            }, tokenSource.Token);
        }

        public static string GetCurrentFile()
        {
            var currentFile = new StringBuilder(Win32.MAX_PATH);
            return
                (int)Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETFULLCURRENTPATH, 0, currentFile) != -1
                    ? currentFile.ToString() : null;
        }

        public static LangType GetCurrentLangType()
        {
            LangType docType = LangType.L_TEXT;
            Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTLANGTYPE, 0, ref docType);
            return docType;
        }

        public static string GetCurrentLangName()
        {
            return GetLangName(GetCurrentLangType());
        }

        public static string GetLangName(LangType langType) 
        {
            var languageName = new StringBuilder(Win32.MAX_PATH);
            return
                (int)Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETLANGUAGENAME, (IntPtr)langType, languageName) != -1
                    ? languageName.ToString() : null;
        }

        public static string GetCurrentLangDesc()
        {
            return GetLangDesc(GetCurrentLangType());
        }

        public static string GetLangDesc(LangType langType) 
        {
            var languageDesc = new StringBuilder(Win32.MAX_PATH);
            return
                (int)Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETLANGUAGEDESC, (IntPtr)langType, languageDesc) != -1
                    ? languageDesc.ToString() : null;
        }

        public static string GetCurrentLanguage() 
        {
            LangType langType = GetCurrentLangType();
            string currentFile = GetCurrentFile();
            string extension = "";
            if (currentFile != null) extension = Path.GetExtension(currentFile).TrimStart('.');

            bool found = false;
            var foundDetectionMethod = Constants.DetectionType.EXTENSION_MAPPING;
            string language = "";

            foreach (var detectionMethod in DetectionOrder)
            {
                switch (detectionMethod)
                {
                    case Constants.DetectionType.CUSTOM_MAPPING:
                        if (!String.IsNullOrWhiteSpace(extension))
                        {
                            try { language = customExtensionMapping[extension]; found = true; }
                            catch (KeyNotFoundException) { found = false; }
                        }
                        break;
                    
                    case Constants.DetectionType.EXTENSION_MAPPING:
                        if (!String.IsNullOrWhiteSpace(extension))
                        {
                            try { language = extensionMapping[extension]; found = true; }
                            catch (KeyNotFoundException) { found = false; }
                        }
                        break;

                    case Constants.DetectionType.LEXER_LANGUAGE:
                        found = true; 
                        switch (langType)
                        {
                            case LangType.L_ADA: language = "Ada"; break;
                            case LangType.L_ASM: language = "Assembler"; break;
                            case LangType.L_ASP: language = "ASP"; break;
                            case LangType.L_AU3: language = "AutoIt"; break;
                            case LangType.L_BASH: language = "Shell Script"; break;
                            case LangType.L_BATCH: language = "Batch"; break;
                            case LangType.L_C: language = "C"; break;
                            case LangType.L_CAML: language = "Caml"; break;
                            case LangType.L_CMAKE: language = "CMake"; break;
                            case LangType.L_COBOL: language = "COBOL"; break;
                            case LangType.L_COFFEESCRIPT: language = "CoffeeScript"; break;
                            case LangType.L_CPP: language = "C++"; break;
                            case LangType.L_CS: language = "C#"; break;
                            case LangType.L_CSS: language = "CSS"; break;
                            case LangType.L_D: language = "D"; break;
                            case LangType.L_DIFF: language = "Diff"; break;
                            case LangType.L_FLASH: language = "Flash"; break;
                            case LangType.L_FORTRAN: language = "Fortran"; break;
                            case LangType.L_FORTRAN_77: language = "Fortran"; break;
                            case LangType.L_GUI4CLI: language = "Gui4Cli"; break;
                            case LangType.L_HASKELL: language = "Haskell"; break;
                            case LangType.L_HTML: language = "HTML"; break;
                            case LangType.L_INI: language = "Ini"; break;
                            case LangType.L_INNO: language = "INNO"; break;
                            case LangType.L_JAVA: language = "Java"; break;
                            case LangType.L_JAVASCRIPT: language = "JavaScript"; break;
                            case LangType.L_JS: language = "JavaScript"; break;
                            case LangType.L_JSON: language = "JSON"; break;
                            case LangType.L_JSP: language = "JSP"; break;
                            case LangType.L_KIX: language = "KIXtart"; break;
                            case LangType.L_LISP: language = "LISP"; break;
                            case LangType.L_LUA: language = "Lua"; break;
                            case LangType.L_MAKEFILE: language = "Makefile"; break;
                            case LangType.L_MATLAB: language = "Matlab"; break;
                            case LangType.L_NSIS: language = "NSIS"; break;
                            case LangType.L_OBJC: language = "Objective-C"; break;
                            case LangType.L_PASCAL: language = "Pascal"; break;
                            case LangType.L_PERL: language = "Perl"; break;
                            case LangType.L_PHP: language = "PHP"; break;
                            case LangType.L_POWERSHELL: language = "PowerShell"; break;
                            case LangType.L_PROPS: language = "Properties"; break;
                            case LangType.L_PS: language = "PostScript"; break;
                            case LangType.L_PYTHON: language = "Python"; break;
                            case LangType.L_R: language = "R"; break;
                            case LangType.L_RC: language = "Resource"; break;
                            case LangType.L_RUBY: language = "Ruby"; break;
                            case LangType.L_SCHEME: language = "Scheme"; break;
                            case LangType.L_SMALLTALK: language = "Smalltalk"; break;
                            case LangType.L_SQL: language = "SQL"; break;
                            case LangType.L_TCL: language = "TCL"; break;
                            case LangType.L_TEX: language = "TeX"; break;
                            case LangType.L_VB: language = "Visual Basic"; break;
                            case LangType.L_VERILOG: language = "Verilog"; break;
                            case LangType.L_VHDL: language = "VHDL"; break;
                            case LangType.L_XML: language = "XML"; break;
                            case LangType.L_YAML: language = "YAML"; break;
                            default: found = false; break;
                        }
                        break;
                }
                if (found)
                {
                    foundDetectionMethod = detectionMethod;
                    break;
                }
            }

            if (!found || String.IsNullOrWhiteSpace(language)) language = "Plain text";
            return language;
        }

        public static bool EnoughTimePassed(DateTime now)
        {
            return _lastActivity < now.AddMilliseconds(-1 * pulseFrequency) && _lastPulse < now.AddMilliseconds(-1 * pulseFrequency);
        }

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
                    var username = match.Groups[2].Value;
                    var password = match.Groups[3].Value;
                    var address = match.Groups[4].Value;
                    var port = match.Groups[5].Value;

                    var credentials = new NetworkCredential(username, password);
                    proxy = new WebProxy(string.Join(":", new string[] { address, port }), true, null, credentials);
                    return proxy;
                }

                var regProxy = new Regex(@"\s*(https?:\/\/)?([^\s@]+):(\d+)\s*");
                match = regProxy.Match(proxyStr);

                if (match.Success)
                {
                    var address = match.Groups[2].Value;
                    var port = int.Parse(match.Groups[3].Value);
                    proxy = new WebProxy(address, port);
                    return proxy;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Exception while parsing the proxy string", ex);
            }
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

                if (timer != null)
                {
                    timer.Stop();
                    timer.Elapsed -= ProcessPulses;
                    timer.Dispose();
                    timer = null;
                }

                try
                {
                    if (pulseProcessor != null) pulseProcessor.Wait();
                }
                catch (Exception ex) { Logger.Error("Exception while waiting for pulse processor to be cancelled", ex); }

                var jsonSerializer = new JavaScriptSerializer();
                Pulse result;
                var unsavedPulses = new List<Pulse>();
                while (pulseQueue.TryDequeue(out result))
                {
                    if (!result.isEmpty()) unsavedPulses.Add(result);
                }
                if (unsavedPulses.Count > 0)
                {
                    var path = ConfigFile.GetUnsavedPulsesFilePath();
                    string json = jsonSerializer.Serialize(unsavedPulses);
                    File.WriteAllText(path, json);
                }
                Logger.FlushEverything();
            }
            catch (Exception ex)
            {
                Logger.Error("Exception while performing plugin shutdown cleanup", ex);
                Logger.FlushEverything();
            }
        }
    }
}
