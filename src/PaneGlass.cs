using System;
using System.Drawing;
using System.Windows.Forms;

namespace TabbedExplorer
{
    /// <summary>
    /// 「侧边栏摊开、盖在内容上」那一下的半透明，一个控件一份视图。
    /// <see cref="Back"/> 是**共享**的那张底图（同一张图给窗格和书签段两个控件用），
    /// 只有 `EmbedForm` 持有并释放它；两个视图各自记「这张图落在我客户坐标的哪一点」。
    ///
    /// ⚠ **不能是 static**（哪怕是共享的那张图也是跟着窗口走的）：每张虚拟桌面一个 `EmbedForm`，
    ///   共用一份就会串台（A 窗口的底图贴到 B 窗口上）。
    /// </summary>
    internal sealed class PaneGlass
    {
        /// <summary>底下那张截图（抓的时刻那地方还是内容）。null = 没有底图。</summary>
        public Bitmap Back;
        /// <summary>底图左上角落在**本控件**客户坐标的哪一点（书签段要减掉自己在窗格里的偏移，Y 是负的）。</summary>
        public int X, Y;
        /// <summary>不透明度（%）：100 = 完全不透明（此时不铺底图，走原来的直画）。</summary>
        public int Pct = 100;

        public bool On { get { return Back != null && Pct < 100; } }

        /// <summary>填充用的 alpha（0~255）。</summary>
        public int Alpha
        {
            get
            {
                int p = Pct < 0 ? 0 : (Pct > 100 ? 100 : Pct);
                return p * 255 / 100;
            }
        }
    }

    /// <summary>
    /// 「只让**背景**透明」的两个小工具。
    ///
    /// 为什么不是整窗按 alpha 合成一遍：那样连标签文字、图标一起变半透明，糊成一片
    /// （用户：「仅背景透明，标签、文字、图标不要透明」）。所以拆成两步 ——
    /// ① <see cref="Backdrop"/> 把「底下长什么样」铺上当底；
    /// ② 之后画**底色 / 高亮色**时走 <see cref="Wash"/> 带上 alpha；
    ///    文字、图标、分隔线照旧用原色不透明地画在最上面。
    /// </summary>
    internal static class GlassPaint
    {
        /// <summary>
        /// 铺底。分三段：
        ///   ① x &lt; `gl.X`（不在内容上那一截 —— 折叠态那条窄带）：底色照旧**不透明**，
        ///      不然鼠标一进一出，这条窄带的深浅会跟着变；
        ///   ② x ≥ `gl.X`：把底图（那一刻的内容）铺上；
        ///   ③ 整个区域再按不透明度盖一层底色 —— 这层就是「背景」。
        /// </summary>
        public static void Backdrop(Graphics g, Control c, PaneGlass gl, Color baseColor)
        {
            int w = c.Width, h = c.Height;
            if (gl == null || !gl.On)
            {
                using (SolidBrush b = new SolidBrush(baseColor)) g.FillRectangle(b, 0, 0, w, h);
                return;
            }
            if (gl.X > 0)
                using (SolidBrush b = new SolidBrush(baseColor)) g.FillRectangle(b, 0, 0, gl.X, h);
            if (gl.Back != null) g.DrawImageUnscaled(gl.Back, gl.X, gl.Y);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(gl.Alpha, baseColor)))
                g.FillRectangle(b, 0, 0, w, h);
        }

        /// <summary>
        /// 底色 / 高亮色怎么给：半透明态下带上 alpha（底下那层是底图 + 底色）。
        /// ⚠ **只管填充**。图标、文字、线条别走这里 —— 用户要的就是它们不透明。
        /// </summary>
        public static Color Wash(PaneGlass gl, Color c)
        {
            if (gl == null || !gl.On) return c;
            return Color.FromArgb(gl.Alpha, c);
        }
    }
}
