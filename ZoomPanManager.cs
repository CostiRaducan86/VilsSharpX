using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VideoStreamPlayer;

/// <summary>
/// Manages zoom and pan transforms for image panes.
/// </summary>
public sealed class ZoomPanManager
{
    public const double MinZoom = 1.0;
    public const double MaxZoom = 40.0;
    public const double ZoomFactor = 1.15;

    private readonly ScaleTransform[] _zooms;
    private readonly TranslateTransform[] _pans;

    private bool _isPanning;
    private int _panningPaneIndex;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    public ZoomPanManager()
    {
        _zooms = new ScaleTransform[3];
        _pans = new TranslateTransform[3];

        for (int i = 0; i < 3; i++)
        {
            _zooms[i] = new ScaleTransform(1.0, 1.0);
            _pans[i] = new TranslateTransform(0.0, 0.0);
        }
    }

    /// <summary>
    /// Gets the zoom transform for the specified pane index (0=A, 1=B, 2=D).
    /// </summary>
    public ScaleTransform GetZoom(int paneIndex) => _zooms[Math.Clamp(paneIndex, 0, 2)];

    /// <summary>
    /// Gets the pan transform for the specified pane index (0=A, 1=B, 2=D).
    /// </summary>
    public TranslateTransform GetPan(int paneIndex) => _pans[Math.Clamp(paneIndex, 0, 2)];

    /// <summary>
    /// Gets both transforms for the specified pane index.
    /// </summary>
    public (ScaleTransform zoom, TranslateTransform pan) GetTransforms(int paneIndex)
    {
        int i = Math.Clamp(paneIndex, 0, 2);
        return (_zooms[i], _pans[i]);
    }

    /// <summary>
    /// Attaches the zoom/pan transforms to the given images.
    /// </summary>
    public void AttachToImages(Image imgA, Image imgB, Image imgD)
    {
        imgA.RenderTransform = CreateTransformGroup(0);
        imgB.RenderTransform = CreateTransformGroup(1);
        imgD.RenderTransform = CreateTransformGroup(2);
    }

    private TransformGroup CreateTransformGroup(int paneIndex)
    {
        return new TransformGroup
        {
            Children = new TransformCollection { _zooms[paneIndex], _pans[paneIndex] }
        };
    }

    /// <summary>
    /// Resets zoom and pan for the specified pane.
    /// </summary>
    public void Reset(int paneIndex)
    {
        var (zoom, pan) = GetTransforms(paneIndex);
        zoom.ScaleX = zoom.ScaleY = 1.0;
        pan.X = pan.Y = 0.0;
    }

    /// <summary>
    /// Whether currently panning.
    /// </summary>
    public bool IsPanning => _isPanning;

    /// <summary>
    /// Index of the pane being panned.
    /// </summary>
    public int PanningPaneIndex => _panningPaneIndex;

    /// <summary>
    /// Handles mouse wheel zoom. Returns true if zoom was applied.
    /// </summary>
    public bool HandleMouseWheel(int paneIndex, MouseWheelEventArgs e, IInputElement parent)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return false;

        var (zoom, pan) = GetTransforms(paneIndex);
        double oldScale = zoom.ScaleX;
        double factor = e.Delta > 0 ? ZoomFactor : (1.0 / ZoomFactor);
        double newScale = Math.Clamp(oldScale * factor, MinZoom, MaxZoom);

        if (Math.Abs(newScale - oldScale) < 1e-9)
            return false;

        // Zoom around mouse position
        Point pParent = e.GetPosition(parent);
        double localX = (pParent.X - pan.X) / oldScale;
        double localY = (pParent.Y - pan.Y) / oldScale;

        pan.X = pParent.X - (localX * newScale);
        pan.Y = pParent.Y - (localY * newScale);
        zoom.ScaleX = zoom.ScaleY = newScale;

        // Reset pan when zoom returns to 1
        if (Math.Abs(newScale - 1.0) < 1e-6)
        {
            pan.X = pan.Y = 0.0;
        }

        return true;
    }

    /// <summary>
    /// Starts panning. Returns true if panning was started.
    /// </summary>
    public bool StartPan(int paneIndex, MouseButtonEventArgs e, Window window, Image img)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return false;

        // Double-click with Ctrl resets zoom/pan
        if (e.ClickCount >= 2)
        {
            Reset(paneIndex);
            return true;
        }

        var (_, pan) = GetTransforms(paneIndex);
        _isPanning = true;
        _panningPaneIndex = paneIndex;
        _panStart = e.GetPosition(window);
        _panStartX = pan.X;
        _panStartY = pan.Y;
        img.CaptureMouse();
        return true;
    }

    /// <summary>
    /// Stops panning.
    /// </summary>
    public void StopPan(Image img)
    {
        if (!_isPanning) return;
        _isPanning = false;
        img.ReleaseMouseCapture();
    }

    /// <summary>
    /// Updates pan position during mouse move.
    /// </summary>
    public void UpdatePan(MouseEventArgs e, Window window)
    {
        if (!_isPanning) return;

        var (_, pan) = GetTransforms(_panningPaneIndex);
        Point cur = e.GetPosition(window);
        Vector d = cur - _panStart;
        pan.X = _panStartX + d.X;
        pan.Y = _panStartY + d.Y;
    }
}
