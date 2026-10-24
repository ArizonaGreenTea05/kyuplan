using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using Orientation = Android.Widget.Orientation;

namespace Kyuplan.Client.AndroidApp.Kit;

/// <summary>The colours of the app: light or dark with the system, the accent is the instance's (its branding), red when it has none.</summary>
public sealed record Palette(Color Background, Color Surface, Color Text, Color Muted, Color Accent, Color OnAccent, Color Line, Color Danger, Color Success, Color Warning)
{
    public static readonly Color DefaultAccent = Color.ParseColor("#C93A26");

    public static bool IsNight(Context context) =>
        (context.Resources!.Configuration!.UiMode & UiMode.NightMask) == UiMode.NightYes;

    /// <param name="accent">The instance's accent colour (<c>#RGB</c> or <c>#RRGGBB</c>); anything else gives the default.</param>
    public static Palette For(Context context, string? accent)
    {
        var night = IsNight(context);
        var accentColor = TryParse(accent) ?? DefaultAccent;
        return night
            ? new Palette(Color.ParseColor("#121214"), Color.ParseColor("#1E1E22"), Color.ParseColor("#F5F0E6"), Color.ParseColor("#A7A3AD"),
                Lighten(accentColor), OnColor(Lighten(accentColor)), Color.ParseColor("#2E2E34"), Color.ParseColor("#FF6B5E"), Color.ParseColor("#5BC781"), Color.ParseColor("#F2B84B"))
            : new Palette(Color.ParseColor("#F7F5F0"), Color.White, Color.ParseColor("#1B1A1F"), Color.ParseColor("#6B6872"),
                accentColor, OnColor(accentColor), Color.ParseColor("#E2DFD8"), Color.ParseColor("#B3261E"), Color.ParseColor("#1E7B45"), Color.ParseColor("#9A5B00"));
    }

    /// <summary>The colour in <paramref name="text"/>, <c>null</c> when it is none.</summary>
    public static Color? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return Color.ParseColor(text.Trim());
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            return null;
        }
    }

    // On a dark background an accent has to be lighter to stay readable.
    private static Color Lighten(Color color) => Color.Argb(255, Mix(color.R), Mix(color.G), Mix(color.B));

    private static int Mix(int channel) => channel + (255 - channel) * 30 / 100;

    /// <summary>Black or white, whichever reads on <paramref name="background"/>.</summary>
    public static Color OnColor(Color background) =>
        (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) > 160 ? Color.ParseColor("#1B1A1F") : Color.White;
}

public enum TextStyle
{
    Title,
    Heading,
    Body,
    Caption,
}

public enum Tone
{
    Neutral,
    Success,
    Warning,
    Danger,
}

/// <summary>
/// The building blocks of every screen: text, cards, buttons, chips and inputs in the colours of the <see cref="Palette"/>. The screens are
/// built in code from these (there are no layout files), so that a page can be drawn from its view model in one place.
/// </summary>
public sealed class Ui(Context context, Palette palette)
{
    private static readonly Handler Main = new(Looper.MainLooper!);

    public Context Context { get; } = context;

    public Palette P { get; } = palette;

    private float Density => Context.Resources!.DisplayMetrics!.Density;

    public int Dp(float value) => (int)(value * Density + 0.5f);

    /// <summary>Runs <paramref name="action"/> on the UI thread, after what is running now.</summary>
    public static void Post(Action action) => Main.Post(action);

    // --- Text ------------------------------------------------------------------------------------------------

    public TextView Text(string? text, TextStyle style = TextStyle.Body, Tone tone = Tone.Neutral, bool muted = false)
    {
        var view = new TextView(Context) { Text = text ?? "" };
        view.SetTextColor(muted ? P.Muted : ToneColor(tone));
        switch (style)
        {
            case TextStyle.Title:
                view.SetTextSize(ComplexUnitType.Sp, 24);
                view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
                break;
            case TextStyle.Heading:
                view.SetTextSize(ComplexUnitType.Sp, 17);
                view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
                break;
            case TextStyle.Caption:
                view.SetTextSize(ComplexUnitType.Sp, 13);
                view.SetTextColor(tone == Tone.Neutral ? P.Muted : ToneColor(tone));
                break;
            default:
                view.SetTextSize(ComplexUnitType.Sp, 15);
                break;
        }

        return view;
    }

    public Color ToneColor(Tone tone) => tone switch
    {
        Tone.Success => P.Success,
        Tone.Warning => P.Warning,
        Tone.Danger => P.Danger,
        _ => P.Text,
    };

    // --- Layout ----------------------------------------------------------------------------------------------

    public LinearLayout Column(params View[] children)
    {
        var layout = new LinearLayout(Context) { Orientation = Orientation.Vertical };
        foreach (var child in children) layout.AddView(child);
        return layout;
    }

    public LinearLayout Row(params View[] children)
    {
        var layout = new LinearLayout(Context) { Orientation = Orientation.Horizontal };
        layout.SetGravity(GravityFlags.CenterVertical);
        foreach (var child in children) layout.AddView(child);
        return layout;
    }

    /// <summary>A rounded sheet on the background that holds the content of one thing (an event, a member, a block of key figures).</summary>
    public LinearLayout Card(params View[] children)
    {
        var card = Column(children);
        card.SetPadding(Dp(16), Dp(14), Dp(16), Dp(14));
        card.Background = Rounded(P.Surface, 16, P.Line);
        return card;
    }

    public View Space(float dp) => new View(Context) { LayoutParameters = new LinearLayout.LayoutParams(1, Dp(dp)) };

    public View Line()
    {
        var line = new View(Context);
        line.SetBackgroundColor(P.Line);
        line.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Math.Max(1, Dp(1)));
        return line;
    }

    public GradientDrawable Rounded(Color fill, float radiusDp, Color? stroke = null)
    {
        var shape = new GradientDrawable();
        shape.SetColor(fill);
        shape.SetCornerRadius(Dp(radiusDp));
        if (stroke is { } line) shape.SetStroke(Math.Max(1, Dp(1)), line);
        return shape;
    }

    /// <summary>Gives a view the touch feedback of the system (a ripple) over <paramref name="background"/>.</summary>
    public Drawable Ripple(Drawable? background)
    {
        var rippleColor = Color.Argb(40, P.Text.R, P.Text.G, P.Text.B);
        return new RippleDrawable(ColorStateList.ValueOf(rippleColor), background, background is null ? new ColorDrawable(Color.White) : null);
    }

    // --- Controls --------------------------------------------------------------------------------------------

    public TextView Button(string text, Action onClick, bool filled = true, bool enabled = true)
    {
        var button = new TextView(Context) { Text = text, Gravity = GravityFlags.Center, Enabled = enabled, Clickable = enabled, Focusable = enabled };
        button.SetTextSize(ComplexUnitType.Sp, 15);
        button.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        button.SetMinHeight(Dp(48));
        button.SetPadding(Dp(20), Dp(10), Dp(20), Dp(10));
        button.SetTextColor(filled ? P.OnAccent : P.Accent);
        button.Background = Ripple(filled ? Rounded(P.Accent, 24) : Rounded(Color.Transparent, 24, P.Accent));
        button.Alpha = enabled ? 1f : 0.45f;
        if (enabled) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A round button with an icon (back, refresh), at least 48dp for the finger.</summary>
    public ImageView IconButton(int drawable, string description, Action onClick)
    {
        var button = new ImageView(Context);
        button.SetImageResource(drawable);
        button.SetColorFilter(P.Text);
        button.ContentDescription = description;
        button.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
        button.Background = Ripple(null);
        button.Clickable = true;
        button.Focusable = true;
        button.LayoutParameters = new LinearLayout.LayoutParams(Dp(48), Dp(48));
        button.Click += (_, _) => onClick();
        return button;
    }

    public TextView Chip(string text, bool selected, Action onClick)
    {
        var chip = new TextView(Context) { Text = text, Gravity = GravityFlags.Center, Clickable = true, Focusable = true };
        chip.SetTextSize(ComplexUnitType.Sp, 14);
        chip.SetMinHeight(Dp(40));
        chip.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
        chip.SetTextColor(selected ? P.OnAccent : P.Text);
        chip.Background = Ripple(selected ? Rounded(P.Accent, 20) : Rounded(P.Surface, 20, P.Line));
        chip.Click += (_, _) => onClick();
        return chip;
    }

    /// <summary>A row of chips that scrolls sideways when it is too wide.</summary>
    public HorizontalScrollView ChipRow(params View[] chips)
    {
        var scroll = new HorizontalScrollView(Context) { HorizontalScrollBarEnabled = false };
        var row = Row(chips);
        for (var i = 0; i < chips.Length; i++) chips[i].LayoutParameters = Params(wrap: true, right: 8);
        scroll.AddView(row);
        return scroll;
    }

    public EditText Input(string hint, string text, Action<string> changed, bool password = false, bool multiline = false)
    {
        var input = new EditText(Context) { Hint = hint };
        input.SetTextSize(ComplexUnitType.Sp, 16);
        input.SetTextColor(P.Text);
        input.SetHintTextColor(P.Muted);
        input.SetMinHeight(Dp(48));
        input.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        input.Background = Rounded(P.Surface, 12, P.Line);
        input.InputType = password
            ? InputTypes.ClassText | InputTypes.TextVariationPassword
            : multiline ? InputTypes.ClassText | InputTypes.TextFlagMultiLine | InputTypes.TextFlagCapSentences : InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        input.Text = text;
        input.TextChanged += (_, _) => changed(input.Text ?? "");
        return input;
    }

    public void Hide(View view)
    {
        if (Context.GetSystemService(Context.InputMethodService) is InputMethodManager keyboard) keyboard.HideSoftInputFromWindow(view.WindowToken, 0);
    }

    public CheckBox Check(bool isChecked, string description, Action<bool> changed)
    {
        var box = new CheckBox(Context) { Checked = isChecked, ContentDescription = description };
        box.ButtonTintList = ColorStateList.ValueOf(P.Accent);
        box.CheckedChange += (_, e) => changed(e.IsChecked);
        return box;
    }

    /// <summary>A coloured line of text over a tinted background: what a page says about itself (an error, a note).</summary>
    public View Notice(string text, Tone tone = Tone.Neutral, params View[] more)
    {
        var color = ToneColor(tone);
        var label = Text(text, TextStyle.Body);
        label.SetTextColor(color);
        var box = Column(label);
        foreach (var view in more) box.AddView(view);
        box.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        box.Background = Rounded(Color.Argb(28, color.R, color.G, color.B), 12, Color.Argb(90, color.R, color.G, color.B));
        return box;
    }

    public ProgressBar Busy()
    {
        var bar = new ProgressBar(Context, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Indeterminate = true };
        bar.IndeterminateTintList = ColorStateList.ValueOf(P.Accent);
        bar.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(4));
        return bar;
    }

    /// <summary>The share (0–1) of a bar, filled with <paramref name="color"/>.</summary>
    public View Bar(double fraction, Color? color = null)
    {
        var track = new LinearLayout(Context) { Orientation = Orientation.Horizontal, WeightSum = 1000 };
        track.Background = Rounded(P.Line, 4);
        var share = (int)Math.Round(Math.Clamp(fraction, 0, 1) * 1000);
        var filled = new View(Context) { Background = Rounded(color ?? P.Accent, 4), LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent, share) };
        track.AddView(filled);
        track.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(6));
        return track;
    }

    /// <summary>A small coloured label (a verdict, an answer).</summary>
    public TextView Badge(string text, Tone tone)
    {
        var color = ToneColor(tone);
        var badge = new TextView(Context) { Text = text };
        badge.SetTextSize(ComplexUnitType.Sp, 12);
        badge.SetTextColor(tone == Tone.Neutral ? P.Muted : color);
        badge.SetPadding(Dp(8), Dp(2), Dp(8), Dp(2));
        badge.Background = Rounded(tone == Tone.Neutral ? P.Line : Color.Argb(32, color.R, color.G, color.B), 10);
        return badge;
    }

    public View BeltSwatch(string? primary, string? secondary)
    {
        var first = Palette.TryParse(primary) ?? P.Line;
        var second = Palette.TryParse(secondary);
        var swatch = new View(Context)
        {
            Background = second is { } stripe
                ? new GradientDrawable(GradientDrawable.Orientation.TopBottom, [(int)first, (int)first, (int)stripe, (int)stripe])
                : Rounded(first, 3),
            LayoutParameters = new LinearLayout.LayoutParams(Dp(28), Dp(10)),
        };
        return swatch;
    }

    // --- Layout parameters -----------------------------------------------------------------------------------

    /// <summary>Layout parameters of a child of a vertical or horizontal LinearLayout; margins in dp.</summary>
    public LinearLayout.LayoutParams Params(bool wrap = false, float weight = 0, float left = 0, float top = 0, float right = 0, float bottom = 0, bool matchHeight = false)
    {
        var layout = new LinearLayout.LayoutParams(
            weight > 0 ? 0 : wrap ? ViewGroup.LayoutParams.WrapContent : ViewGroup.LayoutParams.MatchParent,
            matchHeight ? ViewGroup.LayoutParams.MatchParent : ViewGroup.LayoutParams.WrapContent,
            weight);
        layout.SetMargins(Dp(left), Dp(top), Dp(right), Dp(bottom));
        return layout;
    }

    /// <summary>Adds <paramref name="child"/> to a LinearLayout with a gap of <paramref name="bottom"/> dp below.</summary>
    public void Add(LinearLayout parent, View child, float bottom = 12, float top = 0) => parent.AddView(child, Params(top: top, bottom: bottom));
}
