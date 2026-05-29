namespace ZyncMaster.App.Bridge;

// Window operations the web title bar drives through the bridge (the window is frameless,
// so minimize / maximize / close / move are custom). Implemented by MainWindow; all
// methods marshal to the UI thread internally so the bridge can call them from any thread.
public interface IWindowControl
{
    void Minimize();
    void ToggleMaximize();
    void Close();          // hides to the tray (the app stays resident)
    void BeginDragMove();  // native move drag (fallback; WebView2 app-region handles the primary drag)
}
