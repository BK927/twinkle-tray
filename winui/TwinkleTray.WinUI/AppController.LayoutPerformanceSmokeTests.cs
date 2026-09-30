using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TwinkleTray.Core;
using TwinkleTray.Hardware;

namespace TwinkleTray.WinUI;

internal sealed partial class AppController
{
    private void VerifyLayoutPerformanceForSmokeTest(Action<bool, string> check)
    {
        if (!IsSmokeTest || !IsDemo) throw new InvalidOperationException("Layout performance checks require simulated displays.");
        var originalSettings = Settings;
        var originalMonitors = _monitors;
        var originalFeatures = _features.ToDictionary();
        const string first = "layout-fixture:first", second = "layout-fixture:second";
        var root = (FrameworkElement)_window.Content;
        T Element<T>(string id) where T : FrameworkElement => UiVisualVerification.AuthoredElements(root).OfType<T>()
            .Single(element => AutomationProperties.GetAutomationId(element) == id);
        try
        {
            Settings = new AppSettings();
            var preferences = new MonitorSettings { Name = "First display", ShowContrast = true, Order = 0 };
            var volume = new FeatureSettings { Enabled = true, Name = "Volume", Max = 100 };
            preferences.Features[0x62] = volume;
            preferences.Features[0x60] = new FeatureSettings { Enabled = true, Name = "Input" };
            Settings.Monitors[first] = preferences;
            Settings.Monitors[second] = new MonitorSettings { Name = "Second display", Order = 1 };
            _monitors = [new(first, "First hardware name", "DDC/CI", 55, true, true, 70), new(second, "Second hardware name", "DDC/CI", 40, true, false, null)];
            // Keep this list mutable to exercise in-place hardware capability changes.
            var allowedInputs = new List<uint> { 15, 17 };
            _features.Clear();
            _features[first] = [new(0x62, "Volume", 50, 100, []), new(0x60, "Input", 15, 27, allowedInputs)];
            _window.ApplySettings();

            int generation = _window.RenderGeneration;
            var brightness = Element<Slider>("brightness:" + first);
            var contrast = Element<Slider>("contrast:" + first);
            var volumeSlider = Element<Slider>("feature:" + first + ":98");
            var input = Element<ComboBox>("feature:" + first + ":96");
            _monitors = _monitors.Select(monitor => monitor.Id == first ? monitor with { Brightness = 73, Contrast = 41 } : monitor).ToArray();
            _features[first] = _features[first].Select(feature => feature with { Current = feature.Code == 0x62 ? 23u : 17u }).ToArray();
            _window.RenderMonitors();
            check(_window.RenderGeneration == generation &&
                ReferenceEquals(brightness, Element<Slider>("brightness:" + first)) && brightness.Value == 73 &&
                ReferenceEquals(contrast, Element<Slider>("contrast:" + first)) && contrast.Value == 41 &&
                ReferenceEquals(volumeSlider, Element<Slider>("feature:" + first + ":98")) && volumeSlider.Value == 23 &&
                ReferenceEquals(input, Element<ComboBox>("feature:" + first + ":96")) && input.SelectedItem is ComboBoxItem selected && (uint)selected.Tag == 17,
                "Brightness, contrast and VCP value changes update retained tray controls without a layout rebuild");

            preferences.Name = "Renamed display";
            _window.RenderMonitors();
            check(_window.RenderGeneration == generation + 1 &&
                UiVisualVerification.AuthoredElements(Element<StackPanel>("monitor:" + first)).OfType<TextBlock>().Any(text => text.Text == preferences.Name),
                "In-place monitor rename rebuilds its actual tray heading");

            generation = _window.RenderGeneration;
            preferences.Order = 2;
            _window.RenderMonitors();
            var monitorList = (StackPanel)root.FindName("MonitorList");
            check(_window.RenderGeneration == generation + 1 && AutomationProperties.GetAutomationId(monitorList.Children[0]) == "monitor:" + second,
                "In-place monitor order change rebuilds tray rows in the new order");

            generation = _window.RenderGeneration;
            volume.Max = 60;
            _window.RenderMonitors();
            check(_window.RenderGeneration == generation + 1 && Element<Slider>("feature:" + first + ":98").Maximum == 60,
                "In-place feature maximum change rebuilds the slider with the new bound");

            generation = _window.RenderGeneration;
            volume.IconGlyph = "\uE8D6";
            _window.RenderMonitors();
            check(_window.RenderGeneration == generation + 1 &&
                UiVisualVerification.AuthoredElements(Element<StackPanel>("monitor:" + first)).OfType<FontIcon>().Any(icon => icon.Glyph == volume.IconGlyph),
                "In-place feature icon change rebuilds the actual tray glyph");

            generation = _window.RenderGeneration;
            allowedInputs[1] = 27;
            _window.RenderMonitors();
            input = Element<ComboBox>("feature:" + first + ":96");
            check(_window.RenderGeneration == generation + 1 &&
                input.Items.Cast<ComboBoxItem>().Select(item => (uint)item.Tag).SequenceEqual(new uint[] { 15, 27 }) && input.SelectedItem is null,
                "In-place VCP allowed-value mutation rebuilds the input choices and clears an unavailable selection");

            generation = _window.RenderGeneration;
            _window.RenderMonitors();
            check(_window.RenderGeneration == generation && ReferenceEquals(input, Element<ComboBox>("feature:" + first + ":96")),
                "The updated layout snapshot retains controls on the next unchanged render");
        }
        finally
        {
            Settings = originalSettings;
            _monitors = originalMonitors;
            _features.Clear();
            foreach (var pair in originalFeatures) _features.Add(pair.Key, pair.Value);
            _window.ApplySettings();
        }
    }
}
