using System;
using System.IO;
using Newtonsoft.Json;

namespace RayTraceVS.WPF.Services
{
    public class SettingsService
    {
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RayTraceVS"
        );

        private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");

        private AppSettings settings;

        public SettingsService()
        {
            settings = new AppSettings();
            LoadSettings();
        }

        public string? LastOpenedFilePath
        {
            get => settings.LastOpenedFilePath;
            set
            {
                settings.LastOpenedFilePath = value;
                SaveSettings();
            }
        }

        // メインウィンドウの位置とサイズ
        public WindowBounds? MainWindowBounds
        {
            get => settings.MainWindowBounds;
            set
            {
                settings.MainWindowBounds = value;
                SaveSettings();
            }
        }

        // レンダリングウィンドウの位置とサイズ
        public WindowBounds? RenderWindowBounds
        {
            get => settings.RenderWindowBounds;
            set
            {
                settings.RenderWindowBounds = value;
                SaveSettings();
            }
        }
        
        // パネルの幅
        public double LeftPanelWidth
        {
            get => settings.LeftPanelWidth;
            set
            {
                settings.LeftPanelWidth = value;
                SaveSettings();
            }
        }
        
        public double RightPanelWidth
        {
            get => settings.RightPanelWidth;
            set
            {
                settings.RightPanelWidth = value;
                SaveSettings();
            }
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var loadedSettings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (loadedSettings != null)
                    {
                        settings = loadedSettings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定ファイルの読み込みに失敗しました: {ex.Message}");
                // エラー時はデフォルト設定を使用
                settings = new AppSettings();
            }
        }

        private void SaveSettings()
        {
            try
            {
                // ディレクトリが存在しない場合は作成
                if (!Directory.Exists(SettingsDirectory))
                {
                    Directory.CreateDirectory(SettingsDirectory);
                }

                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"設定ファイルの保存に失敗しました: {ex.Message}");
            }
        }

        private class AppSettings
        {
            public string? LastOpenedFilePath { get; set; }
            public WindowBounds? MainWindowBounds { get; set; }
            public WindowBounds? RenderWindowBounds { get; set; }
            public double LeftPanelWidth { get; set; } = 250;
            public double RightPanelWidth { get; set; } = 300;
        }
    }

    /// <summary>
    /// ウィンドウの位置とサイズ
    /// </summary>
    public class WindowBounds
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool IsMaximized { get; set; }
    }
}
