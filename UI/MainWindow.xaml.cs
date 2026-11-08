using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Ripple.Core;

namespace Ripple.UI;

public partial class MainWindow : Window
{
    private readonly App _app = (App)Application.Current;
    private bool _loading = true;

    public MainWindow()
    {
        InitializeComponent();
        Logo.Source = LogoArt.Word();
        Icon = LogoArt.Render(LogoArt.Initial(), 64, 64);

        var settings = _app.Settings;
        StrengthSlider.Value = settings.Strength;
        DampingSlider.Value = settings.Damping;
        SensitivitySlider.Value = settings.Sensitivity;
        ClickSwitch.IsChecked = settings.ClickSplash;
        FullScreenSwitch.IsChecked = settings.PauseOnFullScreen;
        BatterySwitch.IsChecked = settings.PauseOnBattery;
        StartSwitch.IsChecked = StartWithWindows.IsOn;

        BuildChoices(FitChoices, Enum.GetValues<WallpaperFit>(), settings.Fit, fit => _app.Change(s => s with { Fit = fit }));
        BuildChoices(DetailChoices, Enum.GetValues<WaterDetail>(), settings.Detail, detail => _app.Change(s => s with { Detail = detail }));
        _loading = false;

        ShowPicture();
        SizeChanged += (_, _) => KeepOnScreen();
        Loaded += (_, _) =>
        {
            // A corner-anchored clip can't follow layout from XAML.
            var preview = (Border)Preview.Parent;
            PreviewClip.Rect = new Rect(0, 0, preview.ActualWidth, preview.ActualHeight);
        };
    }

    public void ShowState(string reason)
    {
        var paused = reason.Length > 0;
        StatusText.Text = paused ? "Paused" : "Water on";
        StatusText.Foreground = (Brush)FindResource(paused ? "Muted" : "AccentBright");
        StatusChip.ToolTip = paused ? reason : "The water is live on your desktop.";
        PauseButton.Content = _app.Settings.Paused ? "Resume" : "Pause";
    }

    /// <summary>The settings page is taller than the home page, so never let it grow under the taskbar.</summary>
    private void KeepOnScreen()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var centre = new System.Drawing.Point(
            (int)((Left + (ActualWidth / 2)) * dpi.DpiScaleX),
            (int)((Top + (ActualHeight / 2)) * dpi.DpiScaleY));
        var area = System.Windows.Forms.Screen.FromPoint(centre).WorkingArea;
        var top = area.Top / dpi.DpiScaleY;
        var bottom = area.Bottom / dpi.DpiScaleY;
        if (Top + ActualHeight > bottom)
        {
            Top = Math.Max(top, bottom - ActualHeight);
        }
    }

    private void BuildChoices<T>(UniformGrid grid, T[] values, T current, Action<T> choose) where T : struct, Enum
    {
        foreach (var value in values)
        {
            var button = new Button
            {
                Content = value.ToString(),
                Style = (Style)FindResource(Equals(value, current) ? "BlueButton" : "GreyButton"),
                Margin = new Thickness(0, 0, value.Equals(values[^1]) ? 0 : 5, 0),
                Padding = new Thickness(2, 6, 2, 6),
                FontSize = 12,
                Tag = value,
            };
            button.Click += (_, _) =>
            {
                choose(value);
                foreach (var other in grid.Children.OfType<Button>())
                {
                    other.Style = (Style)FindResource(Equals(other.Tag, value) ? "BlueButton" : "GreyButton");
                }
            };
            grid.Children.Add(button);
        }
    }

    private void ShowPicture()
    {
        var chosen = _app.Settings.WallpaperPath;
        string? path = chosen;
        if (chosen is not null && File.Exists(chosen))
        {
            PictureName.Text = Path.GetFileName(chosen);
            PictureNote.Text = "Your own picture, under the water. Windows' wallpaper is left as it was.";
            UseWindows.Visibility = Visibility.Visible;
        }
        else
        {
            path = WallpaperSource.ReadWindows(new Native.RECT { Left = 0, Top = 0 }).Path;
            PictureName.Text = "Your Windows wallpaper";
            PictureNote.Text = "Ripple follows whatever Windows is showing.";
            UseWindows.Visibility = Visibility.Collapsed;
        }

        Preview.Source = null;
        if (path is null)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = 720;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException or UnauthorizedAccessException or FileFormatException)
            {
                Log.Info($"Preview could not be read: {exception.Message}");
                return null;
            }
        }).ContinueWith(result => Preview.Source = result.Result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnChoosePicture(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a picture for under the water",
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.jfif|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _app.Change(s => s with { WallpaperPath = dialog.FileName });
            ShowPicture();
        }
    }

    private void OnUseWindows(object sender, RoutedEventArgs e)
    {
        _app.Change(s => s with { WallpaperPath = null });
        ShowPicture();
    }

    private void OnPause(object sender, RoutedEventArgs e) => _app.TogglePause();

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        HomePage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        SettingsPage.Visibility = Visibility.Collapsed;
        HomePage.Visibility = Visibility.Visible;
    }

    private void OnQuit(object sender, RoutedEventArgs e) => _app.Quit();

    private void OnSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        var strength = StrengthSlider.Value;
        var damping = DampingSlider.Value;
        var sensitivity = SensitivitySlider.Value;
        _app.Change(s => s with { Strength = strength, Damping = damping, Sensitivity = sensitivity });
    }

    private void OnSwitch(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var click = ClickSwitch.IsChecked == true;
        var full = FullScreenSwitch.IsChecked == true;
        var battery = BatterySwitch.IsChecked == true;
        _app.Change(s => s with { ClickSplash = click, PauseOnFullScreen = full, PauseOnBattery = battery });
    }

    private void OnStartSwitch(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            StartWithWindows.Set(StartSwitch.IsChecked == true);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Info($"Start with Windows could not be changed: {exception.Message}");
            StartSwitch.IsChecked = StartWithWindows.IsOn;
        }
    }
}
