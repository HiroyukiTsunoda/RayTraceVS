using System;
using System.Collections.ObjectModel;
using System.IO;
using RayTraceVS.WPF.Models;

namespace RayTraceVS.WPF.Services
{
    /// <summary>
    /// シーンファイルを読み込んで再保存する（シリアライズ往復の検証用）。
    /// リファクタリングで保存形式（.rtvsのJSON）が変わっていないことを確認するために使う。
    /// load → save の結果を元ファイル（または前回のresave結果）とテキスト比較する。
    ///
    /// 使い方: RayTraceVS.WPF.exe --resave &lt;in.rtvs&gt; &lt;out.rtvs&gt;
    /// </summary>
    public static class SceneResaver
    {
        /// <summary>
        /// 引数が --resave を含むなら再保存を実行して true を返す。
        /// </summary>
        public static bool TryParseAndRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            string? inPath = null;
            string? outPath = null;
            bool isResave = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--resave", StringComparison.OrdinalIgnoreCase))
                {
                    isResave = true;
                    inPath = (i + 1 < args.Length) ? args[++i] : null;
                    outPath = (i + 1 < args.Length) ? args[++i] : null;
                }
            }

            if (!isResave)
                return false;

            if (string.IsNullOrEmpty(inPath) || string.IsNullOrEmpty(outPath))
            {
                Console.WriteLine("[Resave] 使い方: --resave <in.rtvs> <out.rtvs>");
                exitCode = 2;
                return true;
            }

            exitCode = Resave(inPath, outPath);
            return true;
        }

        public static int Resave(string inPath, string outPath)
        {
            try
            {
                if (!File.Exists(inPath)) { Console.WriteLine($"[Resave] 入力が見つかりません: {inPath}"); return 2; }

                var svc = new SceneFileService();
                var (nodes, connections, viewport) = svc.LoadScene(inPath);
                svc.SaveScene(outPath,
                    new ObservableCollection<Node>(nodes),
                    new ObservableCollection<NodeConnection>(connections),
                    viewport);

                Console.WriteLine($"[Resave] {Path.GetFileName(inPath)} -> {Path.GetFileName(outPath)} (nodes={nodes.Count}, connections={connections.Count})");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Resave] 例外: {ex.Message}");
                return 1;
            }
        }
    }
}
