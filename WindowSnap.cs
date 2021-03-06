﻿namespace WindowSnap
{
    using Platform;
    using System;
    using System.Collections.Generic;
    using System.Runtime.InteropServices;
    using System.Windows;
    using System.Windows.Interop;

    public class WindowSnap : IDisposable
    {
        private static readonly Size NoSize = new Size(0, 0);

        private Func<bool> CanSnap;

        private Window _window = null;
        private IntPtr _hWnd = IntPtr.Zero;
        private bool _snapped = false;
        private bool _moving = false;
        private bool _sizing = false;
        private WMSZ _sizingEdge = WMSZ.NONE;
        private Nullable<Size> _offset = null;

        private WindowState ActualState
        {
            get { return Helpers.GetActualState(_hWnd); }
        }

        private bool Unsnapping
        {
            get { return _snapped && Helpers.SizeRestored(_window); }
        }

        private Size Offset
        {
            get { return _offset != null ? (Size)_offset : NoSize; }
        }

        // Events        
        public event EventHandler Snapped = delegate { };
        public event EventHandler Unsnapped = delegate { };
        public event EventHandler EdgeOffsetChanged = delegate { };

        public bool IsSnapped { get { return _snapped; } }

        public WindowSnap(Window window)
        {           
            if (window == null)
            {
                throw new ArgumentNullException("The window cannot be null.");
            }

            _window = window;
            _hWnd = new WindowInteropHelper(window).Handle;

            HwndSource.FromHwnd(_hWnd).AddHook(WindowProc);

            CanSnap = () => SnapSettings.WindowArranging &&
                           (SnapSettings.DockMoving ||
                            SnapSettings.SnapSizing);
        }
        
        #region IDisposable

        private bool _disposed = false;

        ~WindowSnap()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Free other managed objects that 
                // implement IDisposable  
                if (_hWnd != IntPtr.Zero)
                {
                    HwndSource.FromHwnd(_hWnd).RemoveHook(WindowProc);
                    _hWnd = IntPtr.Zero;                    
                }

                _window = null;
            }

            // Release any unmanaged objects
            // set the object references to null

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion IDisposable

        public virtual IntPtr WindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch ((WM)msg)
            {
                case WM.ACTIVATE:
                    {
                        // Initialize offset on window first activation    
                        // Note: this only applies for non-layered style window
                        if (!_window.AllowsTransparency &&
                             _window.WindowStyle != WindowStyle.None &&
                             _offset == null)
                        {
                            var offset = GetEdgeOffset();

                            _offset = offset.Value;

                            EdgeOffsetChanged(this, new EdgeOffsetChangedEventArgs(offset));
                        }

                        break;
                    }

                case WM.SYSCOMMAND:
                    {
                        // To obtain the correct result when testing the value of wParam, 
                        // an application must combine the value 0xFFF0 with the wParam 
                        // value by using the bitwise AND operator.
                        SC command = (SC)(wParam.ToInt32() & 0xFFF0);

                        _sizing = command == SC.SIZE;
                        _moving = command == SC.MOVE;

                        break;
                    }

                case WM.SIZING:
                    {
                        _sizingEdge = (WMSZ)wParam.ToInt32();
                        break;
                    }

                case WM.EXITSIZEMOVE:
                    {
                        _sizingEdge = WMSZ.NONE;
                        break;
                    }

                case WM.WINDOWPOSCHANGING:
                    {
                        WINDOWPOS windowPos = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));

                        if (Helpers.WindowChanged(windowPos))
                        {                            
                            switch (ActualState)
                            {
                                case WindowState.Minimized:

                                    break;

                                case WindowState.Maximized:

                                    Unsnap();

                                    break;

                                default:

                                    // Proceed to snap detection if the current system settings
                                    // allow snapping or if the window is currently snapped
                                    if (CanSnap() || _snapped)
                                    {
                                        RECT rect = new RECT
                                        {
                                            left = windowPos.x,
                                            top = windowPos.y,
                                            right = windowPos.x + windowPos.cx,
                                            bottom = windowPos.y + windowPos.cy
                                        };

                                        Rect location = new Rect(Dpi.ToLogicalX(windowPos.x),
                                                                 Dpi.ToLogicalY(windowPos.y),
                                                                 Dpi.ToLogicalX(windowPos.cx),
                                                                 Dpi.ToLogicalY(windowPos.cy));

                                        // Detecting snapping condition in the WM_WINDOWPOSCHANGING message event
                                        // may not be accurate if using the WINDOWPOS values: when the window is 
                                        // quickly being snapped, the dimension of the window may not always be 
                                        // specified (i.e. cx = cy = 0), in this case the SWP_NOSIZE flag is set
                                        // and we must obtain the window location explicitly instead.

                                        if ((windowPos.flags & SWP.NOSIZE) == SWP.NOSIZE)
                                        {
                                            SafeNativeMethods.GetWindowRect(hWnd, out rect);

                                            location = new Rect(Dpi.ToLogicalX(rect.left),
                                                                Dpi.ToLogicalY(rect.top),
                                                                Dpi.ToLogicalX(rect.right - rect.left),
                                                                Dpi.ToLogicalY(rect.bottom - rect.top));
                                        }

                                        // Get the list of monitors that intersect with the window area
                                        List<Monitor> monitors = SafeNativeMethods.GetDisplayMonitors(rect);

                                        SnapResult snapResult = DetectSnap.IsSnapped(ref location, monitors, Offset);

                                        if (snapResult.IsSnapped)
                                        {
                                            Snap();
                                        }
                                        else if (Unsnapping)
                                        {
                                            Unsnap();
                                        }
                                    }

                                    break;
                            }                            
                        }

                        break;
                    }
            }

            return IntPtr.Zero;
        }

        private void Snap()
        {
            if (!_snapped)
            {
                _snapped = true;

                Snapped(this, new EventArgs());
            }
        }

        private void Unsnap()
        {
            if (_snapped)
            {
                _snapped = false;

                Unsnapped(this, new EventArgs());
            }
        }

        /// <summary>
        /// Obtain the edge vertical and horizontal offset
        /// based on the current type of window
        /// </summary>
        /// <remarks>
        /// ThreeDBorderWindow : WINDOWEDGE + CLIENTEDGE + THICKFRAME
        /// SingleBorderWindow : WINDOWEDGE + THICKFRAME
        /// ToolWindow         : WINDOWEDGE + TOOLWINDOW + THICKFRAME
        /// </remarks>
        /// <returns>The width and height offset</returns>
        private EdgeOffset GetEdgeOffset()
        {
            EdgeOffset edgeOffset = new EdgeOffset();

            WS_EX ws_ex = UnsafeNativeMethods.GetWindowStyleEx(_hWnd);
            WS ws = UnsafeNativeMethods.GetWindowStyle(_hWnd);

            if ((ws_ex & WS_EX.WINDOWEDGE) == WS_EX.WINDOWEDGE)
            {
                edgeOffset.FixedFrame = new Size()
                {
                    Width = SystemParameters.FixedFrameVerticalBorderWidth,
                    Height = SystemParameters.FixedFrameHorizontalBorderHeight
                };
            }

            if ((ws_ex & WS_EX.CLIENTEDGE) == WS_EX.CLIENTEDGE)
            {
                edgeOffset.ThickBorder = new Size()
                {
                    Width = SystemParameters.ThickVerticalBorderWidth,
                    Height = SystemParameters.ThickHorizontalBorderHeight
                };
            }

            if ((ws_ex & WS_EX.TOOLWINDOW) == WS_EX.TOOLWINDOW)
            {
                edgeOffset.ThinBorder = new Size()
                {
                    Width = SystemParameters.ThinVerticalBorderWidth,
                    Height = SystemParameters.ThinHorizontalBorderHeight
                };
            }

            if ((ws & WS.THICKFRAME) == WS.THICKFRAME)
            {
                edgeOffset.ResizeFrame = new Size()
                {
                    Width = SystemParameters.ResizeFrameVerticalBorderWidth,
                    Height = SystemParameters.ResizeFrameHorizontalBorderHeight
                };
            }

            return edgeOffset;
        }
    }
}
