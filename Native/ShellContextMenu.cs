using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using static IndexExplorer.Native.NativeMethods;

namespace IndexExplorer.Native;

/// <summary>앱 고유 메뉴 항목 (윈도우 기본 메뉴 위에 붙는다).</summary>
internal sealed record ExtraMenuItem(string Text, Action Run);

/// <summary>
/// 윈도우 탐색기와 똑같은 우클릭 메뉴 (연결 프로그램, 보내기, 압축, 백신 검사 등 설치된 확장 포함).
/// 서로 다른 폴더에 있는 파일들을 한꺼번에 선택해도 바탕 화면 폴더 기준의 절대 PIDL로 한 메뉴에 띄운다.
/// "삭제·이름 바꾸기·잘라내기·복사"는 색인 규칙에 맞게 앱이 직접 처리하도록 가로챈다.
/// </summary>
internal static class ShellContextMenu
{
    private const uint IdFirst = 1, IdLast = 0x7FFF, ExtraBase = 0x8000;
    private const uint GCS_VERBW = 4;
    private const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_MENUCHAR = 0x120;

    private static IContextMenu2? _cm2;
    private static IContextMenu3? _cm3;

    /// <param name="intercept">가로챌 동사(delete, rename, cut, copy)를 받으면 true를 돌려 기본 동작을 막는다.</param>
    public static void Show(Window owner, IReadOnlyList<string> paths, IReadOnlyList<ExtraMenuItem> extras,
        Func<string, bool> intercept)
    {
        if (paths.Count == 0) return;
        var hwnd = new WindowInteropHelper(owner).Handle;
        var pidls = new List<IntPtr>();
        IntPtr hmenu = IntPtr.Zero, pcm = IntPtr.Zero;
        IShellFolder? desktop = null;
        IContextMenu? cm = null;
        try
        {
            foreach (var p in paths)
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0) pidls.Add(pidl);
            if (pidls.Count == 0) return;

            SHGetDesktopFolder(out desktop);
            var iid = typeof(IContextMenu).GUID;
            if (desktop.GetUIObjectOf(hwnd, (uint)pidls.Count, pidls.ToArray(), ref iid, IntPtr.Zero, out pcm) != 0) return;
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
            _cm2 = cm as IContextMenu2;
            _cm3 = cm as IContextMenu3;

            hmenu = CreatePopupMenu();
            var flags = CMF_NORMAL | CMF_EXPLORE | CMF_CANRENAME;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) flags |= CMF_EXTENDEDVERBS;
            cm.QueryContextMenu(hmenu, 0, IdFirst, IdLast, flags);

            // 앱 고유 항목은 맨 위에 + 구분선
            for (int i = extras.Count - 1; i >= 0; i--)
                InsertMenu(hmenu, 0, MF_BYPOSITION | MF_STRING, (UIntPtr)(ExtraBase + (uint)i), extras[i].Text);
            if (extras.Count > 0) InsertMenu(hmenu, (uint)extras.Count, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);

            GetCursorPos(out var pt);
            var source = HwndSource.FromHwnd(hwnd);
            source?.AddHook(Hook);
            uint cmd;
            try { cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero); }
            finally { source?.RemoveHook(Hook); }
            if (cmd == 0) return;

            if (cmd >= ExtraBase)
            {
                var idx = (int)(cmd - ExtraBase);
                if (idx < extras.Count) owner.Dispatcher.BeginInvoke(extras[idx].Run);
                return;
            }

            var verb = new StringBuilder(256);
            cm.GetCommandString((UIntPtr)(cmd - IdFirst), GCS_VERBW, IntPtr.Zero, verb, verb.Capacity);
            if (verb.Length > 0 && intercept(verb.ToString().ToLowerInvariant())) return;

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                hwnd = hwnd,
                lpVerb = (IntPtr)(cmd - IdFirst),
                lpVerbW = (IntPtr)(cmd - IdFirst),
                nShow = 1,
                ptInvoke = pt,
            };
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) info.fMask |= CMIC_MASK_CONTROL_DOWN;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) info.fMask |= CMIC_MASK_SHIFT_DOWN;
            cm.InvokeCommand(ref info);
        }
        catch (Exception ex)
        {
            App.Log("context menu: " + ex);
        }
        finally
        {
            _cm2 = null; _cm3 = null;
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (pcm != IntPtr.Zero) Marshal.Release(pcm);
            if (desktop != null) Marshal.ReleaseComObject(desktop);
            foreach (var p in pidls) ILFree(p);
        }
    }

    // "연결 프로그램", "보내기" 같은 하위 메뉴가 그려지려면 메뉴 메시지를 셸에 넘겨줘야 한다
    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_INITMENUPOPUP or WM_DRAWITEM or WM_MEASUREITEM or WM_MENUCHAR)
        {
            if (_cm3 != null && _cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out var r) == 0) { handled = true; return r; }
            if (_cm2 != null && _cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0) { handled = true; return IntPtr.Zero; }
        }
        return IntPtr.Zero;
    }
}
