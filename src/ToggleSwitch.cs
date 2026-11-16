using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace quickHide
{
    // 滑动开关（自绘，iOS 风格）：ON=强调蓝胶囊+右侧白圆，OFF=深灰胶囊+左侧白圆；单击切换
    class ToggleSwitch : Control
    {
        bool isChecked;
        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(36, 18);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // 轨道：完整圆角胶囊（左右两个半圆 + 自动连线闭合）
            int pad = 2;
            int trackH = Height - pad * 2;          // 16
            int trackW = Width - pad * 2;           // 40
            int tx = pad, ty = pad;
            int r = trackH / 2;                     // 半径 = 8，直径 = 16

            using (var gp = new GraphicsPath())
            {
                gp.AddArc(tx, ty, r * 2, r * 2, 90, 180);                    // 左半圆
                gp.AddArc(tx + trackW - r * 2 - 1, ty, r * 2, r * 2, 270, 180); // 右半圆
                gp.CloseFigure();
                using (var b = new SolidBrush(isChecked ? Theme.Accent : Theme.Btn))
                    g.FillPath(b, gp);
            }

            // 滑块：正圆（直径 = 轨道高 - 4），ON 靠右 / OFF 靠左，四周留 3px
            int knob = trackH - 6;
            int kx = isChecked ? tx + trackW - knob - 3 : tx + 3;
            int ky = ty + (trackH - knob) / 2;
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, kx, ky, knob, knob);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Checked = !Checked;
        }
    }
}
