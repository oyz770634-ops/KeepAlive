namespace KeepAlive.Framework.UI
{
    public enum UILayer
    {
        Normal = 0,   // 普通层（大厅、背包）
        Popup = 1,    // 弹窗层（确认框、提示框）
        System = 2,   // 系统层（Loading、断线重连）
        HUD = 3       // 游戏内层（血条、摇杆，在3D场景之上但在系统UI之下）
    }
}
