using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>Picks the English or the German text of a layout of a plugin, by the language the client is in (the one <see cref="Strings"/> follows).</summary>
public static class PluginText
{
    public static bool IsGerman => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";

    public static string Pick(string? english, string? german) => PluginRules.Localized(english, german, IsGerman);

    /// <summary><c>null</c> for no text at all, so that a view shows nothing for it.</summary>
    public static string? PickOrNull(string? english, string? german) => Pick(english, german) is { Length: > 0 } text ? text : null;
}

/// <summary>
/// An element of the view of a plugin, built from its layout (<see cref="PluginUiElement"/>). The clients draw one control for each kind of these;
/// nothing of the layout is known to them besides that. <see cref="LanguageChanged"/> makes an element read its texts again.
/// </summary>
public abstract class PluginItemViewModel : ObservableObject
{
    /// <summary>The kind of element, as the layout names it (<c>heading</c>, <c>text</c>, <c>field</c>, <c>list</c>, <c>table</c>, <c>button</c>, <c>form</c>).</summary>
    public abstract string Kind { get; }

    public virtual void LanguageChanged() => OnPropertyChanged(string.Empty);
}

/// <summary>A heading or a paragraph.</summary>
public sealed class PluginTextViewModel(PluginUiElement element) : PluginItemViewModel
{
    public override string Kind => element.Type;

    public bool IsHeading => element.Type == "heading";

    public string Text => PluginText.Pick(element.Text, element.TextDe);
}

/// <summary>One choice of a <c>select</c>.</summary>
public sealed record PluginChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One field of a form: what is typed, and what is wrong with it.</summary>
public sealed partial class PluginFieldViewModel(PluginUiElement element) : PluginItemViewModel
{
    public override string Kind => "field";

    public PluginUiElement Element => element;

    public string Id => element.Id ?? "";

    public string Label => PluginText.Pick(element.Label, element.LabelDe);

    public string? Hint => PluginText.PickOrNull(element.Hint, element.HintDe);

    public bool IsPassword => element.Input == "password";

    public bool IsCheckbox => element.Input == "checkbox";

    public bool IsSelect => element.Input == "select";

    public bool IsMultiline => element.Input == "multiline";

    public bool IsNumber => element.Input == "number";

    /// <summary>A plain one-line text field (also what a number is typed into when the client has no number box).</summary>
    public bool IsText => !IsPassword && !IsCheckbox && !IsSelect && !IsMultiline;

    public int? MaxLength => element.MaxLength;

    public int? Min => element.Min;

    public int? Max => element.Max;

    public bool IsRequired => element.Required;

    public IReadOnlyList<PluginChoice> Choices =>
        [.. (element.Options ?? []).Select(o => new PluginChoice(o.Value, PluginText.Pick(o.Label, o.LabelDe)))];

    /// <summary>The chosen option of a select; an empty text is "nothing chosen".</summary>
    public PluginChoice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Text);
        set => Text = value?.Value ?? "";
    }

    /// <summary>What is typed (text, password, multiline, number) or chosen (select, the value of the option).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(SelectedChoice), nameof(IsEmpty))]
    private string _text = "";

    [ObservableProperty]
    private bool _isChecked;

    /// <summary>A password is stored (it is never sent to the client): leaving the field empty keeps it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(StoredSecretHint))]
    private bool _hasStoredSecret;

    public string? StoredSecretHint => IsPassword && HasStoredSecret ? Strings.Plugin_SecretStored : null;

    /// <summary>Nothing typed or chosen (a checkbox is never empty).</summary>
    public bool IsEmpty => !IsCheckbox && string.IsNullOrWhiteSpace(Text);

    /// <summary>What is wrong with the value, in words; <c>null</c> when it is fine.</summary>
    public string? Problem
    {
        get
        {
            if (IsRequired && IsEmpty && !(IsPassword && HasStoredSecret)) return Strings.Plugin_FieldRequired;

            return PluginRules.CheckValue(element, ToJson()) is null ? null : Strings.Plugin_FieldInvalid;
        }
    }

    /// <summary>Puts the default (or the stored value) into the field.</summary>
    public void Load(JsonElement value)
    {
        HasStoredSecret = false;
        switch (element.Input)
        {
            case "checkbox":
                IsChecked = value.ValueKind == JsonValueKind.True;
                break;

            case "number":
                Text = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number.ToString(CultureInfo.InvariantCulture) : ""; // numbers are typed with a point, whatever the language is
                break;

            default:
                Text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
                break;
        }

        OnPropertyChanged(nameof(Problem));
    }

    /// <summary>The default of the layout as the value a stored one would be.</summary>
    public JsonElement DefaultValue() => element.Input switch
    {
        "checkbox" => JsonSerializer.SerializeToElement(string.Equals(element.Default, "true", StringComparison.OrdinalIgnoreCase)),
        "number" => double.TryParse(element.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? JsonSerializer.SerializeToElement(number) : default,
        _ => JsonSerializer.SerializeToElement(element.Default ?? ""),
    };

    /// <summary>The value as it is sent: a flag, a number (null when the box is empty or no number), or the text as typed (a password and a multiline text; the rest is trimmed).</summary>
    public JsonElement ToJson()
    {
        switch (element.Input)
        {
            case "checkbox":
                return JsonSerializer.SerializeToElement(IsChecked);

            case "number":
                return double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                    ? JsonSerializer.SerializeToElement(number)
                    : string.IsNullOrWhiteSpace(Text) ? JsonSerializer.SerializeToElement<string?>(null) : JsonSerializer.SerializeToElement(Text); // text that is no number: the rules say so

            default:
                return JsonSerializer.SerializeToElement(IsPassword || IsMultiline ? Text : Text.Trim());
        }
    }

    public override void LanguageChanged()
    {
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One row of a list: its fields, and which stored row it is (a row that was there before keeps the passwords stored for it).</summary>
public sealed class PluginRowViewModel(IReadOnlyList<PluginFieldViewModel> fields, int? keepIndex)
{
    public IReadOnlyList<PluginFieldViewModel> Fields => fields;

    /// <summary>The position of this row in the stored list; <c>null</c> for a row that was added now.</summary>
    public int? KeepIndex => keepIndex;

    /// <summary>Nothing typed in any field and no stored password: a row nobody filled in is not saved.</summary>
    public bool IsEmpty => fields.All(f => f.IsCheckbox ? !f.IsChecked : f.IsEmpty && !f.HasStoredSecret);

    public string? Problem => IsEmpty ? null : fields.Select(f => f.Problem).FirstOrDefault(p => p is not null);

    public JsonElement ToJson()
    {
        var values = fields.ToDictionary(f => f.Id, f => f.ToJson());
        var row = values.ToDictionary(p => p.Key, p => (object?)p.Value);
        if (keepIndex is { } index) row[PluginRules.KeepProperty] = index;
        return JsonSerializer.SerializeToElement(row);
    }
}

/// <summary>A list of rows of fields the user can add to and remove from.</summary>
public sealed partial class PluginListViewModel : PluginItemViewModel
{
    private readonly PluginUiElement _element;

    public PluginListViewModel(PluginUiElement element)
    {
        _element = element;
        Rows.CollectionChanged += (_, _) => Changed();
    }

    public override string Kind => "list";

    public string Id => _element.Id ?? "";

    public string Label => PluginText.Pick(_element.Label, _element.LabelDe);

    public string? Hint => PluginText.PickOrNull(_element.Hint, _element.HintDe);

    public int MaxRows => _element.Max ?? 50;

    public ObservableCollection<PluginRowViewModel> Rows { get; } = [];

    public bool CanAdd => Rows.Count < MaxRows;

    public string? Problem => Rows.Select(r => r.Problem).FirstOrDefault(p => p is not null);

    /// <summary>Raised when a row or a value in it changed.</summary>
    public event EventHandler? ValueChanged;

    private void Changed()
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(Problem));
        AddRowCommand.NotifyCanExecuteChanged();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A row of empty fields (the field view models of a row are made from the same elements as the list's).</summary>
    private PluginRowViewModel NewRow(int? keepIndex)
    {
        var fields = (_element.Elements ?? []).Where(e => e.Type == "field").Select(e => new PluginFieldViewModel(e)).ToList();
        foreach (var field in fields)
        {
            field.Load(field.DefaultValue());
            field.PropertyChanged += (_, _) => Changed();
        }

        return new PluginRowViewModel(fields, keepIndex);
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddRow() => Rows.Add(NewRow(null));

    [RelayCommand]
    private void RemoveRow(PluginRowViewModel? row)
    {
        if (row is not null) Rows.Remove(row);
    }

    /// <summary>Puts the stored rows into the list. <paramref name="secrets"/> are the paths of the password fields that have a stored value (<see cref="PluginSettingsDto.SecretsSet"/>).</summary>
    public void Load(JsonElement rows, IReadOnlyCollection<string> secrets)
    {
        Rows.Clear();
        if (rows.ValueKind != JsonValueKind.Array) return;

        var index = 0;
        foreach (var item in rows.EnumerateArray())
        {
            var stored = item.TryGetProperty(PluginRules.KeepProperty, out var keep) && keep.TryGetInt32(out var i) ? i : index;
            var row = NewRow(stored);
            foreach (var field in row.Fields)
            {
                if (item.TryGetProperty(field.Id, out var value)) field.Load(value);
                if (field.IsPassword) field.HasStoredSecret = secrets.Contains($"{Id}/{stored}/{field.Id}");
            }

            Rows.Add(row);
            index++;
        }
    }

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(Rows.Where(r => !r.IsEmpty).Select(r => r.ToJson()).ToList());

    public override void LanguageChanged()
    {
        OnPropertyChanged(string.Empty);
        foreach (var field in Rows.SelectMany(r => r.Fields)) field.LanguageChanged();
    }
}

/// <summary>
/// The items of a layout that is a form (the settings form of a data source, or a <c>form</c> of a page): headings, fields and lists. Knows the values typed,
/// whether they were changed and whether they are fine.
/// </summary>
public sealed class PluginFormViewModel : ObservableObject
{
    private string _baseline = "";

    public PluginFormViewModel(IReadOnlyList<PluginUiElement> elements)
    {
        Items = [.. elements.Select(Create).OfType<PluginItemViewModel>()];
        foreach (var item in Items)
        {
            switch (item)
            {
                case PluginFieldViewModel field: field.PropertyChanged += (_, _) => Changed(); break;
                case PluginListViewModel list: list.ValueChanged += (_, _) => Changed(); break;
            }
        }

        Load(null);
    }

    public PluginFormViewModel(PluginUiLayout layout) : this(layout.Elements)
    {
    }

    private static PluginItemViewModel? Create(PluginUiElement element) => element.Type switch
    {
        "heading" or "text" => new PluginTextViewModel(element),
        "field" => new PluginFieldViewModel(element),
        "list" => new PluginListViewModel(element),
        _ => null,
    };

    public IReadOnlyList<PluginItemViewModel> Items { get; }

    public IEnumerable<PluginFieldViewModel> Fields => Items.OfType<PluginFieldViewModel>();

    /// <summary>Raised whenever something typed changed.</summary>
    public event EventHandler? ValueChanged;

    private void Changed()
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(IsChanged));
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fills the form with the stored settings (the defaults of the layout where there are none, and for a <c>null</c>); typed passwords are cleared.</summary>
    public void Load(PluginSettingsDto? stored)
    {
        var secrets = stored?.SecretsSet ?? [];
        foreach (var item in Items)
        {
            if (item is PluginFieldViewModel field)
            {
                field.Load(stored is not null && stored.Values.TryGetValue(field.Id, out var value) ? value : field.DefaultValue());
                if (field.IsPassword) field.HasStoredSecret = secrets.Contains(field.Id);
            }
            else if (item is PluginListViewModel list)
            {
                list.Load(stored is not null && stored.Values.TryGetValue(list.Id, out var rows) ? rows : default, secrets);
            }
        }

        _baseline = Snapshot();
        Changed();
    }

    /// <summary>What a save sends: the value of every field and list, by id.</summary>
    public IReadOnlyDictionary<string, JsonElement> Values()
    {
        var values = new Dictionary<string, JsonElement>();
        foreach (var item in Items)
        {
            if (item is PluginFieldViewModel field) values[field.Id] = field.ToJson();
            else if (item is PluginListViewModel list) values[list.Id] = list.ToJson();
        }

        return values;
    }

    private string Snapshot() => JsonSerializer.Serialize(Values().OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value));

    /// <summary>Something was typed that differs from what was loaded (a typed password counts).</summary>
    public bool IsChanged => Snapshot() != _baseline;

    /// <summary>The first thing that is wrong with what is typed, in words; <c>null</c> when it is fine.</summary>
    public string? Problem => Items.Select(i => i switch
    {
        PluginFieldViewModel input => input.Problem,
        PluginListViewModel rows => rows.Problem,
        _ => null,
    }).FirstOrDefault(p => p is not null);

    public bool IsValid => Problem is null;

    public void LanguageChanged()
    {
        foreach (var item in Items) item.LanguageChanged();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A table: the rows an endpoint of the plugin answered.</summary>
public sealed partial class PluginTableViewModel(PluginUiElement element) : PluginItemViewModel
{
    public override string Kind => "table";

    public string? Title => PluginText.PickOrNull(element.Label, element.LabelDe);

    public string? SourcePath => element.Source;

    public IReadOnlyList<string> Headers => [.. (element.Columns ?? []).Select(c => PluginText.Pick(c.Label, c.LabelDe))];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(EmptyText))]
    private IReadOnlyList<IReadOnlyList<string>> _rows = [];

    public bool IsEmpty => Rows.Count == 0;

    public string EmptyText => PluginText.PickOrNull(element.Text, element.TextDe) ?? Strings.Plugin_TableEmpty;

    /// <summary>Takes the rows of the endpoint's answer (an array of objects; anything else is no rows).</summary>
    public void Load(JsonElement answer)
    {
        var columns = element.Columns ?? [];
        if (answer.ValueKind != JsonValueKind.Array)
        {
            Rows = [];
            return;
        }

        Rows = [.. answer.EnumerateArray().Select(item => (IReadOnlyList<string>)[.. columns.Select(c => Cell(item, c.Key))])];
    }

    private static string Cell(JsonElement item, string key)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(key, out var value)) return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            JsonValueKind.True => Strings.Plugin_Yes,
            JsonValueKind.False => Strings.Plugin_No,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number.ToString("0.##", CultureInfo.CurrentCulture),
            _ => value.ToString(),
        };
    }
}

/// <summary>A button that calls an endpoint of the plugin.</summary>
public sealed class PluginButtonViewModel(PluginUiElement element, Func<PluginUiAction, JsonElement?, Task> run) : PluginItemViewModel
{
    public override string Kind => "button";

    public string Label => PluginText.Pick(element.Label, element.LabelDe);

    public IAsyncRelayCommand Command { get; } = new AsyncRelayCommand(() => run(element.Action!, null));
}

/// <summary>A form of a page: fields, and a button that sends them to an endpoint of the plugin.</summary>
public sealed class PluginFormBlockViewModel : PluginItemViewModel
{
    public PluginFormBlockViewModel(PluginUiElement element, Func<PluginUiAction, JsonElement?, Task> run)
    {
        Element = element;
        Form = new PluginFormViewModel(element.Elements ?? []);
        Submit = new AsyncRelayCommand(async () =>
        {
            await run(element.Action!, JsonSerializer.SerializeToElement(Form.Values()));
        }, () => Form.IsValid);
        Form.ValueChanged += (_, _) => Submit.NotifyCanExecuteChanged();
    }

    public PluginUiElement Element { get; }

    public override string Kind => "form";

    public PluginFormViewModel Form { get; }

    public string SubmitLabel => PluginText.Pick(Element.Label, Element.LabelDe);

    public IAsyncRelayCommand Submit { get; }

    public override void LanguageChanged()
    {
        Form.LanguageChanged();
        OnPropertyChanged(string.Empty);
    }
}
