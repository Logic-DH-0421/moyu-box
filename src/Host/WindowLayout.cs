using System;
using System.Drawing;
using System.Windows.Forms;

namespace MoyuBox
{
    internal sealed partial class MainWindow
    {
        bool autoExpanded;
        Rectangle compactBounds;
        FormWindowState lastWindowState = FormWindowState.Normal;

        void ConfigureCompactWindow()
        {
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = new Size(Math.Min(760, work.Width), Math.Min(520, work.Height));
            Size = new Size(Math.Min(Math.Max(760, Math.Min(1040, store.State.width)), Math.Max(1, work.Width - 24)), Math.Min(Math.Max(520, Math.Min(720, store.State.height)), Math.Max(1, work.Height - 24)));
            StartPosition = FormStartPosition.Manual;
            Location = new Point(work.Left + (work.Width - Width) / 2, work.Top + (work.Height - Height) / 2);
            compactBounds = Bounds;
            Resize += delegate
            {
                if (WindowState != lastWindowState)
                {
                    // A manual restore is respected while switching between project tabs.
                    if (lastWindowState == FormWindowState.Maximized && WindowState == FormWindowState.Normal) autoExpanded = false;
                    Telemetry.Write("INFO", "window", "state.changed", "Window state changed", new System.Collections.Generic.Dictionary<string, object> { { "from", lastWindowState.ToString() }, { "to", WindowState.ToString() }, { "reason", autoExpanded ? "project_auto" : "window_event" } });
                    lastWindowState = WindowState; Publish();
                }
            };
        }
        public void PrepareProjectWindow(Project project)
        {
            if (!store.State.autoMaximizeProjects || project.kind == "external" || WindowState != FormWindowState.Normal) return;
            compactBounds = Bounds; autoExpanded = true;
            WindowState = FormWindowState.Maximized; Publish();
        }
        void ReturnToCompactWindow()
        {
            if (!autoExpanded) return;
            autoExpanded = false;
            if (WindowState != FormWindowState.Maximized) return;
            WindowState = FormWindowState.Normal;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            Rectangle restored = compactBounds;
            restored.Width = Math.Min(restored.Width, work.Width); restored.Height = Math.Min(restored.Height, work.Height);
            restored.X = Math.Max(work.Left, Math.Min(work.Right - restored.Width, restored.X));
            restored.Y = Math.Max(work.Top, Math.Min(work.Bottom - restored.Height, restored.Y));
            Bounds = restored; Publish();
        }
        Rectangle beforeFloatingBounds;
        FormWindowState beforeFloatingState;
        Size beforeFloatingMinimum;
        bool projectFloating, beforeFloatingTopMost, beforeFloatingAuto;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wparam,IntPtr lparam);
        public void DragProjectWindow() { if(projectFloating){ReleaseCapture();SendMessage(Handle,0xA1,new IntPtr(2),IntPtr.Zero);} }
        public void SetProjectFloating(bool enabled)
        {
            if(enabled==projectFloating)return;
            projectFloating=enabled;
            if(enabled){beforeFloatingBounds=Bounds;beforeFloatingState=WindowState;beforeFloatingMinimum=MinimumSize;beforeFloatingTopMost=TopMost;beforeFloatingAuto=autoExpanded;WindowState=FormWindowState.Normal;MinimumSize=new Size(300,280);Size=new Size(370,490);var work=Screen.FromControl(this).WorkingArea;Location=new Point(work.Right-Width-12,work.Top+12);TopMost=true;SetProjectFocus(true);}
            else {SetProjectFocus(false);TopMost=beforeFloatingTopMost;MinimumSize=beforeFloatingMinimum;WindowState=FormWindowState.Normal;Bounds=beforeFloatingBounds;WindowState=beforeFloatingState;autoExpanded=beforeFloatingAuto;}
            Telemetry.Write("INFO","window","floating.changed","Host project floating mode changed",new System.Collections.Generic.Dictionary<string,object>{{"enabled",enabled}});Publish();
        }
        void ToggleProjectWindow()
        {
            if (WindowState == FormWindowState.Maximized) { autoExpanded = false; WindowState = FormWindowState.Normal; }
            else { compactBounds = Bounds; autoExpanded = sessions.ActiveId != null; WindowState = FormWindowState.Maximized; }
            Publish();
        }
    }
}
