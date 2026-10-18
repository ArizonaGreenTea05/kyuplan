using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kyuplan.Client.Api;
using Kyuplan.Client.Resources;
using Kyuplan.Contracts;

namespace Kyuplan.Client.ViewModels;

/// <summary>An amount of money typed into a field: the text, whether it reads as an amount, and whether it differs from what is saved.</summary>
public sealed partial class MoneyField : ObservableObject
{
    private decimal _saved;

    public MoneyField(decimal amount = 0)
    {
        _saved = amount;
        _text = Display.MoneyInput(amount);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(Amount), nameof(HasChange))]
    private string _text;

    public bool IsValid => Display.TryParseMoney(Text, out _);

    /// <summary>The typed amount; 0 while the text is not valid.</summary>
    public decimal Amount => Display.TryParseMoney(Text, out var amount) ? amount : 0;

    /// <summary>The amount the server holds.</summary>
    public decimal Saved => _saved;

    /// <summary>The text is not valid, or reads as another amount than the server holds.</summary>
    public bool HasChange => !IsValid || Amount != _saved;

    /// <summary>Takes this as what the server holds and shows it.</summary>
    public void Reset(decimal amount)
    {
        _saved = amount;
        Text = Display.MoneyInput(amount);
        OnPropertyChanged(nameof(Saved));
        OnPropertyChanged(nameof(HasChange));
    }
}

/// <summary>One line of the price list: a belt and the price that is added to the general fee when a member receives it.</summary>
public sealed class BeltPriceRow(BeltOption belt, MoneyField price)
{
    public BeltOption Belt => belt;

    public string Name => belt.Label;

    public MoneyField Price => price;
}

/// <summary>
/// The price list of the exams: the general fee every member pays, and the additional price of each belt. Used by both clients. Reading it
/// needs <c>payments.view</c>, changing it <c>prices.edit</c>.
/// </summary>
public sealed partial class ExamPricesViewModel : PageViewModel
{
    private readonly IPlannerApi _api;
    private Func<string>? _notice;

    public ExamPricesViewModel(ShellViewModel shell, IPlannerApi api) : base(shell)
    {
        _api = api;
        General = new MoneyField();
        General.PropertyChanged += (_, _) => NotifyChanged();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsBusy)) NotifyChanged();
        };
    }

    public override string Title => Strings.Nav_Prices;

    protected override string? ViewPermission => Permission.PaymentsView;

    /// <summary>What belongs to the group does not matter here: the prices are the club's.</summary>
    protected override bool ReloadsWithContext => false;

    /// <summary>Belts may have been added on their page; not while the user is typing.</summary>
    protected override bool ReloadsOnActivation => !HasChanges;

    public bool CanEdit => Shell.Can(Permission.PricesEdit);

    protected override void OnPermissionsChanged()
    {
        base.OnPermissionsChanged();
        NotifyChanged();
    }

    // --- The list ----------------------------------------------------------------------------------------------

    /// <summary>The fee every member pays.</summary>
    public MoneyField General { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBelts))]
    private IReadOnlyList<BeltPriceRow> _rows = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBelts))]
    private bool _hasLoaded;

    public bool HasNoBelts => HasLoaded && Rows.Count == 0;

    /// <summary>Asks the API again, e.g. after a failed load.</summary>
    [RelayCommand]
    private Task RetryAsync() => InvalidateAsync();

    protected override async Task ReloadAsync() => await LoadAsync(async ct =>
    {
        var belts = await _api.GetBeltsAsync(ct);
        var prices = await _api.GetExamPricesAsync(ct);
        ct.ThrowIfCancellationRequested();

        Show(belts, prices);
        HasLoaded = true;
    });

    private void Show(IReadOnlyList<BeltDto> belts, ExamPricesDto prices)
    {
        foreach (var row in Rows) row.Price.PropertyChanged -= OnFieldChanged;

        var surcharges = prices.BeltPrices.ToDictionary(p => p.BeltId, p => p.Surcharge);
        General.Reset(prices.GeneralFee);
        Rows = [.. belts.Select(b => new BeltPriceRow(new BeltOption(b), new MoneyField(surcharges.GetValueOrDefault(b.Id))))];

        foreach (var row in Rows) row.Price.PropertyChanged += OnFieldChanged;
        NotifyChanged();
    }

    private void OnFieldChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => NotifyChanged();

    // --- Result of the last action -------------------------------------------------------------------------

    public string NoticeText => _notice?.Invoke() ?? "";

    public bool HasNotice => _notice is not null;

    private void SetNotice(Func<string>? notice)
    {
        _notice = notice;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(HasNotice));
    }

    // --- Saving ------------------------------------------------------------------------------------------------

    /// <summary>A price differs from what the server holds (or is not a valid amount).</summary>
    public bool HasChanges => General.HasChange || Rows.Any(r => r.Price.HasChange);

    /// <summary>Some price is not a valid amount: it is pointed out instead of saved.</summary>
    public bool HasInvalid => !General.IsValid || Rows.Any(r => !r.Price.IsValid);

    public bool CanSave => CanEdit && HasChanges && !HasInvalid && !IsBusy;

    public bool CanDiscard => CanEdit && HasChanges && !IsBusy;

    private void NotifyChanged()
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(HasInvalid));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDiscard));
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!CanSave) return;

        var input = new ExamPricesDto(
            General.Amount,
            [.. Rows.Where(r => r.Price.Amount > 0).Select(r => new BeltPriceDto(r.Belt.Id!.Value, r.Price.Amount))]);

        ExamPricesDto? saved = null;
        await LoadAsync(async ct => saved = await _api.SetExamPricesAsync(input, ct));
        if (saved is null) return; // refused: the reason is in ErrorMessage, what was typed stays

        var surcharges = saved.BeltPrices.ToDictionary(p => p.BeltId, p => p.Surcharge);
        General.Reset(saved.GeneralFee);
        foreach (var row in Rows) row.Price.Reset(surcharges.GetValueOrDefault(row.Belt.Id!.Value));

        SetNotice(() => Strings.Prices_Saved);
        NotifyChanged();
    }

    /// <summary>Puts the fields back to what the server holds.</summary>
    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        General.Reset(General.Saved);
        foreach (var row in Rows) row.Price.Reset(row.Price.Saved);
        SetNotice(null);
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var row in Rows) row.Belt.Refresh();
        OnPropertyChanged(nameof(NoticeText));
    }
}
