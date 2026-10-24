using Kyuplan.Client.LinuxApp.Kit;
using Kyuplan.Client.Resources;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Client.LinuxApp.Pages;

/// <summary>What an exam costs: the general fee and, per belt, the stamp fee and the price of the belt.</summary>
internal static class PricesPage
{
    public static Gtk.Widget Build(UiContext context, ExamPricesViewModel page)
    {
        Gtk.Entry Money(MoneyField field, string name)
        {
            var entry = Ui.Entry(field, nameof(MoneyField.Text), name: name);
            entry.WidthChars = 9;
            entry.Hexpand = false;
            entry.Halign = Gtk.Align.End;
            entry.Sensitive = page.CanEdit;
            return entry;
        }

        var general = Ui.Row(Strings.Prices_General, Strings.Prices_Note, Money(page.General, Strings.Prices_General));
        var belts = Ui.BoxedList(page, () => page.Rows, row =>
        {
            var r = Ui.Row(row.Name, null, null, ColorClass.Belt(row.Belt.Color, row.Belt.SecondaryColor));
            r.AddSuffix(Ui.Caption(Strings.Prices_StampFee, "dim-label", "caption"));
            r.AddSuffix(Money(row.StampFee, $"{row.Name}: {Strings.Prices_StampFee}"));
            r.AddSuffix(Ui.Caption(Strings.Prices_BeltPrice, "dim-label", "caption"));
            r.AddSuffix(Money(row.BeltPrice, $"{row.Name}: {Strings.Prices_BeltPrice}"));
            return r;
        }, [nameof(ExamPricesViewModel.Rows)]);

        var body = Ui.VBox(14,
            Widgets.Header(page.Title, Ui.Muted(Strings.Prices_Intro)),
            Widgets.ErrorBanner(page, page.RetryCommand), Widgets.Notice(page),
            Ui.Label(Strings.Prices_Invalid, "error").VisibleWhen(page, nameof(ExamPricesViewModel.HasInvalid)),
            Ui.BoxedList().Tap(l => l.Append(general)),
            Ui.Label(Strings.Prices_Belts, "heading").Start(),
            Ui.Muted(Strings.Prices_BeltsHint).Css("caption"),
            Ui.Muted(Strings.Prices_NoBelts).VisibleWhen(page, nameof(ExamPricesViewModel.HasNoBelts)),
            belts,
            Ui.HBox(8, Ui.Primary(Strings.Prices_Save, page.SaveCommand), Ui.Button(Strings.Prices_Discard, page.DiscardCommand)).VisibleWhen(page, nameof(ExamPricesViewModel.CanEdit)));
        return Widgets.PageFrame(page, body, 900);
    }
}
