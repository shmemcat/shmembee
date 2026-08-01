using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MusicBeePlugin
{
    internal sealed class MusicBeeTheme : IDisposable
    {
        private MusicBeeTheme(
            Color background,
            Color surface,
            Color raisedSurface,
            Color border,
            Color foreground,
            Color muted,
            Color accent,
            Color selection,
            Color disabled,
            Color modified,
            bool windowBordersSkinned,
            Font font)
        {
            Background = background;
            Surface = surface;
            RaisedSurface = raisedSurface;
            Border = border;
            Foreground = foreground;
            Muted = muted;
            Accent = accent;
            Selection = selection;
            Disabled = disabled;
            Modified = modified;
            WindowBordersSkinned = windowBordersSkinned;
            Font = font;
        }

        public Color Background { get; }

        public Color Surface { get; }

        public Color RaisedSurface { get; }

        public Color Border { get; }

        public Color Foreground { get; }

        public Color Muted { get; }

        public Color Accent { get; }

        public Color Selection { get; }

        public Color Disabled { get; }

        public Color Modified { get; }

        public bool WindowBordersSkinned { get; }

        public Color Success => Color.FromArgb(76, 175, 125);

        public Color Warning => Color.FromArgb(229, 169, 75);

        public Color Danger => Color.FromArgb(224, 98, 98);

        public Font Font { get; }

        public static Color BlendColours(Color first, Color second, float amount)
        {
            return Blend(first, second, amount);
        }

        public static MusicBeeTheme CreateDefault()
        {
            Color background = Color.FromArgb(24, 26, 28);
            Color foreground = Color.FromArgb(232, 234, 236);
            Color surface = Color.FromArgb(34, 37, 40);
            return new MusicBeeTheme(
                background,
                surface,
                Color.FromArgb(43, 47, 50),
                Color.FromArgb(65, 70, 74),
                foreground,
                Color.FromArgb(157, 163, 168),
                Color.FromArgb(0, 174, 172),
                Color.FromArgb(0, 145, 143),
                Color.FromArgb(112, 117, 121),
                Color.FromArgb(91, 118, 133),
                false,
                new Font(
                    SystemFonts.MessageBoxFont.FontFamily,
                    Math.Max(SystemFonts.MessageBoxFont.Size, 9F),
                    FontStyle.Regular,
                    GraphicsUnit.Point));
        }

        public static MusicBeeTheme FromApi(Plugin.MusicBeeApiInterface api)
        {
            Color background = ReadColour(
                api,
                Plugin.SkinElement.SkinSubPanel,
                Plugin.ElementState.ElementStateDefault,
                Plugin.ElementComponent.ComponentBackground,
                Color.FromArgb(28, 29, 30));
            Color foreground = ReadColour(
                api,
                Plugin.SkinElement.SkinSubPanel,
                Plugin.ElementState.ElementStateDefault,
                Plugin.ElementComponent.ComponentForeground,
                Color.FromArgb(229, 231, 233));
            Color surface = ReadColour(
                api,
                Plugin.SkinElement.SkinInputPanel,
                Plugin.ElementState.ElementStateDefault,
                Plugin.ElementComponent.ComponentBackground,
                Color.FromArgb(37, 39, 40));
            Color border = ReadColour(
                api,
                Plugin.SkinElement.SkinInputControl,
                Plugin.ElementState.ElementStateDefault,
                Plugin.ElementComponent.ComponentBorder,
                Color.FromArgb(65, 68, 70));
            Color accent = ReadColour(
                api,
                Plugin.SkinElement.SkinButton,
                Plugin.ElementState.ElementStateHighlight,
                Plugin.ElementComponent.ComponentBackground,
                Color.FromArgb(0, 172, 170));
            Color selection = ReadColour(
                api,
                Plugin.SkinElement.SkinButton,
                Plugin.ElementState.ElementStateHighlight,
                Plugin.ElementComponent.ComponentBackground,
                accent);
            Color disabled = ReadColour(
                api,
                Plugin.SkinElement.SkinSubPanel,
                Plugin.ElementState.ElementStateDisabled,
                Plugin.ElementComponent.ComponentForeground,
                Blend(foreground, background, 0.55F));
            Color modified = ReadColour(
                api,
                Plugin.SkinElement.SkinSubPanel,
                Plugin.ElementState.ElementStateModified,
                Plugin.ElementComponent.ComponentBackground,
                Blend(surface, accent, 0.2F));
            bool windowBordersSkinned = api.Setting_IsWindowBordersSkinned != null
                && api.Setting_IsWindowBordersSkinned();
            Font sourceFont = api.Setting_GetDefaultFont == null
                ? SystemFonts.MessageBoxFont
                : api.Setting_GetDefaultFont();
            Font font = new Font(
                sourceFont.FontFamily,
                Math.Max(sourceFont.Size, 9F),
                sourceFont.Style,
                GraphicsUnit.Point);
            return new MusicBeeTheme(
                background,
                surface,
                Blend(surface, foreground, 0.06F),
                border,
                foreground,
                Blend(foreground, background, 0.43F),
                accent,
                selection,
                disabled,
                modified,
                windowBordersSkinned,
                font);
        }

        public void Apply(Control root)
        {
            root.Font = Font;
            ApplyRecursive(root);
        }

        public void ApplyDarkTitleBar(Form form)
        {
            if (WindowBordersSkinned
                || Environment.OSVersion.Version.Major < 10
                || !form.IsHandleCreated)
            {
                return;
            }

            int enabled = 1;
            int attribute = 20;
            int result = DwmSetWindowAttribute(
                form.Handle,
                attribute,
                ref enabled,
                sizeof(int));
            if (result != 0)
            {
                attribute = 19;
                DwmSetWindowAttribute(form.Handle, attribute, ref enabled, sizeof(int));
            }
        }

        public void Dispose()
        {
            Font.Dispose();
        }

        private void ApplyRecursive(Control control)
        {
            control.Font = Font;
            control.ForeColor = Foreground;
            if (control is DataGridView grid)
            {
                ApplyGrid(grid);
            }
            else if (control is TextBoxBase
                || control is ListBox
                || control is ComboBox
                || control is ListView)
            {
                control.BackColor = Surface;
            }
            else if (control is Button button)
            {
                button.BackColor = RaisedSurface;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = Blend(
                    RaisedSurface,
                    Accent,
                    0.22F);
                button.FlatAppearance.MouseDownBackColor = Blend(
                    RaisedSurface,
                    Accent,
                    0.35F);
                button.UseVisualStyleBackColor = false;
            }
            else
            {
                control.BackColor = Background;
            }

            foreach (Control child in control.Controls)
            {
                ApplyRecursive(child);
            }
        }

        private void ApplyGrid(DataGridView grid)
        {
            grid.BackgroundColor = Background;
            grid.GridColor = Border;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersDefaultCellStyle.BackColor = RaisedSurface;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Foreground;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = RaisedSurface;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Foreground;
            grid.DefaultCellStyle.BackColor = Surface;
            grid.DefaultCellStyle.ForeColor = Foreground;
            grid.DefaultCellStyle.SelectionBackColor = Selection;
            grid.DefaultCellStyle.SelectionForeColor = Foreground;
            grid.AlternatingRowsDefaultCellStyle.BackColor =
                Blend(Surface, Background, 0.22F);
        }

        private static Color ReadColour(
            Plugin.MusicBeeApiInterface api,
            Plugin.SkinElement element,
            Plugin.ElementState state,
            Plugin.ElementComponent component,
            Color fallback)
        {
            if (api.Setting_GetSkinElementColour == null)
            {
                return fallback;
            }

            int value = api.Setting_GetSkinElementColour(element, state, component);
            Color result = Color.FromArgb(
                (value >> 16) & 0xff,
                (value >> 8) & 0xff,
                value & 0xff);
            return result;
        }

        private static Color Blend(Color first, Color second, float amount)
        {
            float inverse = 1F - amount;
            return Color.FromArgb(
                (int)((first.R * inverse) + (second.R * amount)),
                (int)((first.G * inverse) + (second.G * amount)),
                (int)((first.B * inverse) + (second.B * amount)));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int value,
            int valueSize);
    }
}
