using System.Runtime.ExceptionServices;
using VESPER;

namespace Vesper.Windows.Tests;

public sealed class SettingsAccessibilityTests
{
    [Fact]
    public void SettingsChoicesExposeTheirNamesRoleAndCurrentValue()
    {
        OnSta(() =>
        {
            using var form = new SettingsForm();
            _ = form.Handle;
            VesperChoice[] choices = Descendants(form).OfType<VesperChoice>().ToArray();

            string[] expectedNames =
            [
                "Simultaneous downloads", "Preset 1 unit", "Preset 2 unit", "Preset 3 unit",
                "Download connections", "Proxy mode", "When the browser captures a download",
                "Color theme", "Download row height"
            ];
            Assert.Equal(expectedNames.Length, choices.Length);
            foreach (string name in expectedNames)
                Assert.Contains(choices, choice => choice.AccessibilityObject.Name == name);

            foreach (VesperChoice choice in choices)
            {
                Assert.Equal(AccessibleRole.ComboBox, choice.AccessibilityObject.Role);
                Assert.Equal(choice.AccessibleName, choice.AccessibilityObject.Name);
                Assert.Equal(choice.SelectedText, choice.AccessibilityObject.Value);
            }

            VesperChoice theme = choices.Single(choice => choice.AccessibleName == "Color theme");
            theme.AccessibilityObject.Value = "Dark";
            Assert.Equal("Dark", theme.SelectedText);
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(8)), "The settings UI test did not finish.");
        error?.Throw();
    }
}
