using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using Windows.ApplicationModel;
using Windows.Storage;

namespace System.Runtime.CompilerServices
{
    public static class IsExternalInit { }
}

namespace Typedown.Core
{
    public static class Config
    {
        public static bool IsMicaSupported { get; } = Environment.OSVersion.Version.Build >= 22000;

        public static IReadOnlyList<string> WebView2Args { get; } = new List<string>()
        {
            "--disable-web-security",
            "--allow-file-access-from-files",
            "--flag-switches-begin",
            "--enable-features=msOverlayScrollbarWinStyle",
            "--flag-switches-end"
        };

        public static JsonSerializerSettings EditorJsonSerializerSettings = new()
        {
            ContractResolver = new DefaultContractResolver()
            {
                NamingStrategy = new CamelCaseNamingStrategy(true, true)
            },
            MaxDepth = 256
        };

        public static string GetLocalFolderPath()
        {
            if (IsPortable)
                return EnsureDirectory(Path.Combine(AppContext.BaseDirectory, "Data"));
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch (Exception)
            {
                return EnsureDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppName));
            }
        }

        private static string EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            return path;
        }

        public static string AppName => "Typedown";

        public static bool IsPackaged { get; private set; }

        // Unpackaged builds with a "portable" marker file next to the executable keep all data in the program folder
        public static bool IsPortable { get; private set; }

        static Config()
        {
            try
            {
                IsPackaged = Package.Current != null;
            }
            catch
            {
                IsPackaged = false;
            }
            IsPortable = !IsPackaged && File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));
        }
    }
}
