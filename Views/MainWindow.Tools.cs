using System.Diagnostics;
using System.IO;
using System.Windows;
using IndexExplorer.Services;
using IndexExplorer.ViewModels;

namespace IndexExplorer.Views;

/// <summary>윈도우 도구 바로 열기: 메모장, 명령 프롬프트, PowerShell.</summary>
public partial class MainWindow
{
    private static string SysExe(string rel) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), rel);

    private void Notepad_Click(object sender, RoutedEventArgs e) => OpenInNotepad(Selected());
    private void Cmd_Click(object sender, RoutedEventArgs e) => OpenConsoleHere(powerShell: false);
    private void PowerShell_Click(object sender, RoutedEventArgs e) => OpenConsoleHere(powerShell: true);

    /// <summary>선택한 파일을 메모장으로 (선택이 없으면 새 메모장).</summary>
    private void OpenInNotepad(IReadOnlyList<ListItem> items)
    {
        var files = items.Where(i => i.Kind == ItemKind.File && !i.Missing && File.Exists(i.Path)).Take(10).ToList();
        try
        {
            if (files.Count == 0) { Process.Start(SysExe("notepad.exe")); return; }
            foreach (var f in files) Process.Start(SysExe("notepad.exe"), $"\"{f.Path}\"");
        }
        catch (Exception ex) { Toast("메모장을 열 수 없습니다", ex.Message); }
    }

    /// <summary>
    /// 명령 창을 열 폴더. 원본 폴더는 그 폴더, 색인 폴더는 파일이 흩어져 있으므로
    /// 선택한 파일의 폴더 → 그 색인의 저장 폴더 → 내 문서 순.
    /// </summary>
    private string ConsoleDir()
    {
        if (_loc.Kind == LocKind.Dir && Directory.Exists(_loc.Path)) return _loc.Path;
        var sel = Selected().FirstOrDefault(i => i.Kind is ItemKind.File or ItemKind.Dir && !i.Missing);
        if (sel != null)
        {
            var d = sel.Kind == ItemKind.Dir ? sel.Path : PathUtil.Dir(sel.Path);
            if (Directory.Exists(d)) return d;
        }
        if (_loc.Kind == LocKind.IndexFolder && StorageOf(_loc.FolderId).Dir is { } sd && Directory.Exists(sd)) return sd;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void OpenConsoleHere(bool powerShell)
    {
        var dir = ConsoleDir();
        try
        {
            var exe = powerShell ? SysExe(@"WindowsPowerShell\v1.0\powershell.exe") : SysExe("cmd.exe");
            Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
            if (!_loc.IsPhysical) Toast($"{(powerShell ? "PowerShell" : "명령 프롬프트")}을 열었습니다", dir);
        }
        catch (Exception ex) { Toast("열 수 없습니다", ex.Message); }
    }
}
