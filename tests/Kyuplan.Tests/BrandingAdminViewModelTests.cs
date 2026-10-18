using System.Globalization;
using Microsoft.Extensions.Options;
using Kyuplan.Client.Api;
using Kyuplan.Client.Localization;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;
using static Kyuplan.Tests.TestAccounts;

namespace Kyuplan.Tests;

/// <summary>The administrators' branding page: what it offers when, and what it asks the API to do.</summary>
public sealed class BrandingAdminViewModelTests : IDisposable
{
    private readonly FakePlannerApi _api = new();
    private readonly LanguageService _language = new();
    private readonly ShellViewModel _shell;
    private readonly BrandingAdminViewModel _vm;

    public BrandingAdminViewModelTests()
    {
        _language.Select("en");
        _api.Account = Dto(1, "admin", "admin", [], true);

        _shell = new ShellViewModel(_api, _language, new LoginSession());
        _vm = new BrandingAdminViewModel(_shell, _api, Options.Create(new PlannerClientOptions { BaseAddress = "https://planner.example.org/" }));
    }

    public void Dispose()
    {
        // Selecting a language applies its culture process-wide; do not leak it into other tests.
        CultureInfo.DefaultThreadCurrentCulture = null;
        CultureInfo.DefaultThreadCurrentUICulture = null;
    }

    private async Task<BrandingAdminViewModel> ShownAsync()
    {
        await _vm.ActivateAsync();
        return _vm;
    }

    private static BrandingImageRow Row(BrandingAdminViewModel vm, string slot) => vm.Images.Single(r => r.Slot == slot);

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A file the page takes for a PNG: the signature, then whatever follows.</summary>
    private static byte[] Png(params byte[] more) => [.. PngSignature, .. more];

    private static Task PickAsync(BrandingAdminViewModel vm, string slot, params byte[] more) =>
        vm.PickImageAsync(slot, Png(more).Length, () => new MemoryStream(Png(more)));

    /// <summary>Picks an image for the slot and saves it: what the clients show changes, and the page counts as customized.</summary>
    private static async Task SavedAsync(BrandingAdminViewModel vm, string slot, params byte[] more)
    {
        await PickAsync(vm, slot, more);
        await vm.SaveImagesCommand.ExecuteAsync(null);
    }

    // --- Who gets to see it ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_page_for_somebody_who_may_not_see_it_does_not_even_ask_the_api()
    {
        _api.Account = Dto(2, "Anna", "Anna", [], false);

        var vm = await ShownAsync();

        Assert.False(vm.CanView);
        Assert.False(vm.IsLoaded);
        Assert.Equal(0, _api.BrandingSettingsRequests);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task An_administrator_sees_the_saved_values_and_the_five_slots()
    {
        var vm = await ShownAsync();

        Assert.True(vm.CanView);
        Assert.True(vm.IsLoaded);
        Assert.Equal("Branding", vm.Title);
        Assert.Equal("kyuplan", vm.FormProductName);
        Assert.Equal("My Club", vm.FormOrganizationName);
        Assert.Equal("#2563eb", vm.FormAccentColor);
        Assert.False(vm.IsCustomized);

        Assert.Equal(BrandingSlots.All, vm.Images.Select(i => i.Slot));
        Assert.Equal(
            ["Logo, light surfaces", "Logo, dark surfaces", "Tab icon"],
            vm.Images.Select(i => i.Title));
        Assert.Equal([false, true, false], vm.Images.Select(i => i.OnDark)); // their previews sit on a dark surface
        Assert.Equal([true, false, true], vm.Images.Select(i => i.HasImage));
        Assert.Equal("image/png,image/jpeg,image/gif,image/bmp,image/svg+xml", Row(vm, BrandingSlots.Logo).Accept); // an SVG is fine: both clients draw it
        Assert.DoesNotContain("image/webp", Row(vm, BrandingSlots.Logo).Accept); // the desktop client could not
    }

    [Fact]
    public async Task A_preview_is_an_absolute_address_that_is_new_every_time_something_changed()
    {
        var vm = await ShownAsync();
        var first = Row(vm, BrandingSlots.Logo).PreviewUrl;
        Assert.Equal("https://planner.example.org/api/branding/logo?v=1", first);

        await SavedAsync(vm, BrandingSlots.Logo, 1, 2, 3);

        // The address of an image never changes, so without this a browser would go on showing the old logo.
        Assert.Equal("https://planner.example.org/api/branding/logo?v=2", Row(vm, BrandingSlots.Logo).PreviewUrl);
    }

    [Fact]
    public async Task A_failed_load_can_be_tried_again()
    {
        _api.BrandingSettingsFailure = new PlannerApiException("The API is busy.");
        var vm = await ShownAsync();
        Assert.Equal("The API is busy.", vm.ErrorMessage);
        Assert.False(vm.IsLoaded);

        _api.BrandingSettingsFailure = null;
        await vm.RetryCommand.ExecuteAsync(null);

        Assert.False(vm.HasError);
        Assert.True(vm.IsLoaded);
    }

    // --- The texts and the colour ----------------------------------------------------------------------------

    [Fact]
    public async Task Save_is_offered_only_for_a_change_the_api_accepts()
    {
        var vm = await ShownAsync();
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));

        vm.FormProductName = "PlanIt";
        Assert.True(vm.CanSave);
        Assert.True(vm.SaveCommand.CanExecute(null));

        vm.FormProductName = "  kyuplan "; // the same, only with blanks around it
        Assert.False(vm.CanSave);

        vm.FormAccentColor = "#2563EB"; // the same colour in capitals
        Assert.False(vm.CanSave);

        vm.FormAccentColor = "#1a5fb4";
        Assert.True(vm.CanSave);

        vm.FormOrganizationName = "";
        Assert.True(vm.CanSave); // a club's name may be left out
        Assert.Null(vm.OrganizationNameProblem);
    }

    [Theory]
    [InlineData("green")]
    [InlineData("#12345")]
    [InlineData("#gg0000")]
    [InlineData("")]
    public async Task A_colour_that_is_none_is_a_problem_and_cannot_be_saved(string color)
    {
        var vm = await ShownAsync();

        vm.FormAccentColor = color;

        Assert.Equal("A colour like #2563eb or #26e is needed.", vm.AccentColorProblem);
        Assert.False(vm.CanSave);
        Assert.Equal("#2563eb", vm.AccentPickerValue); // the picker keeps the colour that is saved
    }

    [Fact]
    public async Task A_product_name_is_required_and_has_a_limit()
    {
        var vm = await ShownAsync();

        vm.FormProductName = "  ";
        Assert.Equal("A product name is required, at most 60 characters long.", vm.ProductNameProblem);
        Assert.False(vm.CanSave);

        vm.FormProductName = new string('x', BrandingRules.MaxProductNameLength + 1);
        Assert.NotNull(vm.ProductNameProblem);
        Assert.False(vm.CanSave);

        vm.FormProductName = new string('x', BrandingRules.MaxProductNameLength);
        Assert.Null(vm.ProductNameProblem);
        Assert.True(vm.CanSave);

        vm.FormOrganizationName = new string('x', BrandingRules.MaxOrganizationNameLength + 1);
        Assert.Equal("At most 80 characters, no control characters.", vm.OrganizationNameProblem);
        Assert.False(vm.CanSave);
    }

    [Theory]
    [InlineData("#abc", "#aabbcc")]
    [InlineData("#1A5FB4", "#1a5fb4")]
    public async Task A_colour_picker_is_given_the_typed_colour_spelled_out(string typed, string expected)
    {
        var vm = await ShownAsync();

        vm.FormAccentColor = typed;

        Assert.Equal(expected, vm.AccentPickerValue);
    }

    [Fact]
    public async Task Saving_sends_the_trimmed_values_and_the_page_shows_what_is_saved_now()
    {
        var vm = await ShownAsync();
        vm.FormProductName = "  PlanIt ";
        vm.FormOrganizationName = " Karate Club Beispiel ";
        vm.FormAccentColor = " #1a5fb4";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["save-branding"], _api.BrandingCalls);
        Assert.Equal(new BrandingInputDto("PlanIt", "Karate Club Beispiel", "#1a5fb4", ThemeIds.Default, ThemeIds.Default, "€"), _api.BrandingInputs.Single());
        Assert.Equal("Saved.", vm.NoticeText);
        Assert.True(vm.HasNotice);
        Assert.True(vm.IsCustomized);
        Assert.Equal("PlanIt", vm.FormProductName);
        Assert.Equal("#1a5fb4", vm.FormAccentColor);
        Assert.False(vm.CanSave); // nothing left to save
        Assert.True(vm.CanReset);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task A_failed_save_keeps_what_was_typed_and_says_why()
    {
        var vm = await ShownAsync();
        vm.FormProductName = "PlanIt";
        _api.FailWith = new PlannerApiException("The API refused it.");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("The API refused it.", vm.ErrorMessage);
        Assert.False(vm.HasNotice);
        Assert.Equal("PlanIt", vm.FormProductName);
        Assert.False(vm.IsCustomized);

        _api.FailWith = null;
        Assert.True(vm.CanSave); // it can be tried again
    }

    [Fact]
    public async Task Correcting_the_form_after_a_failure_clears_the_old_error()
    {
        var vm = await ShownAsync();
        vm.FormProductName = "PlanIt";
        _api.FailWith = new PlannerApiException("The API refused it.");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        vm.FormProductName = "PlanIt 2";

        Assert.False(vm.HasError);
    }

    // --- What this client shows -------------------------------------------------------------------------------

    [Fact]
    public async Task What_was_saved_is_shown_by_this_client_at_once_without_waiting_for_the_next_look()
    {
        var vm = await ShownAsync();
        await _shell.Brand.LoadAsync();
        var changed = 0;
        _shell.Brand.Changed += (_, _) => changed++;
        vm.FormProductName = "PlanIt";
        vm.FormAccentColor = "#1a5fb4";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("PlanIt", _shell.Brand.ProductName);
        Assert.Equal("#1a5fb4", _shell.Brand.AccentColor);
        Assert.Equal(1, changed); // told once, so the accent colour is applied once
    }

    [Fact]
    public async Task An_uploaded_or_removed_image_and_a_reset_are_shown_by_this_client_at_once_too()
    {
        var vm = await ShownAsync();
        await _shell.Brand.LoadAsync();
        Assert.False(_shell.Brand.HasLogo);

        await SavedAsync(vm, BrandingSlots.Logo, 1, 2, 3);
        var uploaded = _shell.Brand.LogoUrl;
        Assert.NotNull(uploaded);
        Assert.NotNull(_shell.Brand.LogoOnDarkUrl); // a missing dark logo is the light one

        await SavedAsync(vm, BrandingSlots.Logo, 4, 5, 6);
        Assert.NotEqual(uploaded, _shell.Brand.LogoUrl); // the same address would leave the old image on screen

        vm.RemoveImageCommand.Execute(Row(vm, BrandingSlots.Logo));
        await vm.SaveImagesCommand.ExecuteAsync(null);
        Assert.False(_shell.Brand.HasLogo);

        await SavedAsync(vm, BrandingSlots.Icon, 1, 2, 3);
        vm.FormProductName = "PlanIt";
        await vm.SaveCommand.ExecuteAsync(null);
        vm.AskResetCommand.Execute(null);
        await vm.ConfirmResetCommand.ExecuteAsync(null);

        Assert.Equal("kyuplan", _shell.Brand.ProductName);
        Assert.Equal("My Club", _shell.Brand.OrganizationName);
    }

    [Fact]
    public async Task A_change_that_failed_is_not_shown()
    {
        var vm = await ShownAsync();
        await _shell.Brand.LoadAsync();
        var requests = _api.BrandingRequests;
        vm.FormProductName = "PlanIt";
        _api.FailWith = new PlannerApiException("The API refused it.");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(requests, _api.BrandingRequests); // nothing changed, so nothing is asked
        Assert.Equal("kyuplan", _shell.Brand.ProductName);
    }

    // --- The images ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_picked_image_is_shown_by_its_slot_but_not_sent_until_the_images_are_saved()
    {
        var vm = await ShownAsync();
        Assert.False(Row(vm, BrandingSlots.LogoOnDark).HasImage);
        Assert.False(vm.CanSaveImages); // nothing to save yet
        Assert.False(vm.SaveImagesCommand.CanExecute(null));

        await PickAsync(vm, BrandingSlots.LogoOnDark, 7, 8, 9);

        var row = Row(vm, BrandingSlots.LogoOnDark);
        Assert.True(row.HasImage);
        Assert.True(row.IsPending);
        Assert.StartsWith("data:image/png;base64,", row.PreviewUrl); // the file itself: there is no address for it yet
        Assert.Equal(Png(7, 8, 9), Convert.FromBase64String(row.PreviewUrl!["data:image/png;base64,".Length..]));
        Assert.False(Row(vm, BrandingSlots.Logo).IsPending); // the other slots are as they were
        Assert.True(vm.CanSaveImages);
        Assert.True(vm.SaveImagesCommand.CanExecute(null));
        Assert.Empty(_api.BrandingCalls);
        Assert.False(vm.IsCustomized);
        Assert.False(vm.HasNotice);
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Saving_the_images_sends_them_as_they_are_and_the_slots_show_the_saved_ones()
    {
        var vm = await ShownAsync();
        await PickAsync(vm, BrandingSlots.LogoOnDark, 7, 8, 9);

        await vm.SaveImagesCommand.ExecuteAsync(null);

        Assert.Equal(["upload-image:logo-on-dark"], _api.BrandingCalls);
        Assert.Equal(Png(7, 8, 9), _api.BrandingUploads.Single());
        var row = Row(vm, BrandingSlots.LogoOnDark);
        Assert.True(row.HasImage);
        Assert.False(row.IsPending);
        Assert.StartsWith("https://planner.example.org/api/branding/logo-on-dark?v=", row.PreviewUrl);
        Assert.Equal("Images saved.", vm.NoticeText);
        Assert.True(vm.IsCustomized);
        Assert.False(vm.CanSaveImages); // nothing left to save
        Assert.False(vm.HasError);
    }

    [Fact]
    public async Task Pictures_and_removals_are_saved_together_slot_after_slot()
    {
        var vm = await ShownAsync();
        await PickAsync(vm, BrandingSlots.Icon, 1);
        await PickAsync(vm, BrandingSlots.LogoOnDark, 2);
        vm.RemoveImageCommand.Execute(Row(vm, BrandingSlots.Logo));
        Assert.Empty(_api.BrandingCalls);

        await vm.SaveImagesCommand.ExecuteAsync(null);

        Assert.Equal(["remove-image:logo", "upload-image:logo-on-dark", "upload-image:icon"], _api.BrandingCalls);
        Assert.Equal([Png(2), Png(1)], _api.BrandingUploads);
        Assert.Equal([false, true, true], vm.Images.Select(i => i.HasImage));
        Assert.All(vm.Images, row => Assert.False(row.IsPending));
    }

    [Fact]
    public async Task A_refused_image_keeps_it_and_the_ones_after_it_picked_and_the_ones_before_it_are_saved()
    {
        var vm = await ShownAsync();
        await PickAsync(vm, BrandingSlots.Logo, 1);
        await PickAsync(vm, BrandingSlots.LogoOnDark, 2);
        await PickAsync(vm, BrandingSlots.Icon, 3);
        _api.BrandingImageRefusedSlot = BrandingSlots.LogoOnDark;

        await vm.SaveImagesCommand.ExecuteAsync(null);

        Assert.Equal(["upload-image:logo", "upload-image:logo-on-dark"], _api.BrandingCalls); // it stopped at the refused one
        Assert.Equal("The API refused this image.", vm.ErrorMessage);
        Assert.False(vm.HasNotice); // not "saved": not everything is
        Assert.Equal([false, true, true], vm.Images.Select(i => i.IsPending));
        Assert.True(vm.IsCustomized); // the first one did change what the API says
        Assert.True(vm.CanSaveImages);

        _api.BrandingImageRefusedSlot = null;
        await vm.SaveImagesCommand.ExecuteAsync(null); // trying again sends what is left

        Assert.Equal(["upload-image:logo", "upload-image:logo-on-dark", "upload-image:logo-on-dark", "upload-image:icon"], _api.BrandingCalls);
        Assert.False(vm.HasError);
        Assert.Equal("Images saved.", vm.NoticeText);
        Assert.False(vm.CanSaveImages);
    }

    [Fact]
    public async Task A_refused_first_image_changes_nothing_and_keeps_everything_picked()
    {
        var vm = await ShownAsync();
        await PickAsync(vm, BrandingSlots.Logo, 1);
        _api.FailWith = new PlannerApiException("The API is busy.");

        await vm.SaveImagesCommand.ExecuteAsync(null);

        Assert.Equal("The API is busy.", vm.ErrorMessage);
        Assert.False(vm.HasNotice);
        Assert.True(Row(vm, BrandingSlots.Logo).IsPending);
        Assert.False(vm.IsCustomized);

        _api.FailWith = null;
        Assert.True(vm.CanSaveImages);
    }

    [Fact]
    public async Task An_image_that_is_too_large_is_refused_without_reading_the_file()
    {
        var vm = await ShownAsync();
        var opened = false;

        await vm.PickImageAsync(BrandingSlots.Logo, BrandingRules.MaxImageBytes + 1, () =>
        {
            opened = true;
            return new MemoryStream();
        });

        Assert.Equal("The file is larger than 1024 KB.", vm.ErrorMessage);
        Assert.False(opened);
        Assert.False(Row(vm, BrandingSlots.Logo).IsPending);
        Assert.False(vm.CanSaveImages);
    }

    [Fact]
    public async Task A_file_that_announced_less_than_it_holds_is_refused_too()
    {
        var vm = await ShownAsync();

        await vm.PickImageAsync(BrandingSlots.Logo, 10, () => new MemoryStream([.. PngSignature, .. new byte[BrandingRules.MaxImageBytes]]));

        Assert.Equal("The file is larger than 1024 KB.", vm.ErrorMessage);
        Assert.False(vm.CanSaveImages);
    }

    [Theory]
    [InlineData(BrandingSlots.Logo, new byte[] { 1, 2, 3 })] // no image at all
    [InlineData(BrandingSlots.Logo, new byte[] { 0, 0, 1, 0, 1, 0, 0 })] // an ICO is for the tab only
    public async Task A_file_the_slot_does_not_take_is_refused_when_it_is_picked_and_names_the_formats(string slot, byte[] content)
    {
        var vm = await ShownAsync();

        await vm.PickImageAsync(slot, content.Length, () => new MemoryStream(content));

        Assert.Equal("Logo, light surfaces: the file must be an image of one of these formats: PNG, JPEG, GIF, BMP, SVG.", vm.ErrorMessage);
        Assert.False(Row(vm, slot).IsPending);
        Assert.False(vm.CanSaveImages);
        Assert.Empty(_api.BrandingCalls);
    }

    [Fact]
    public async Task An_unreadable_file_is_reported_and_picks_nothing()
    {
        var vm = await ShownAsync();

        await vm.PickImageAsync(BrandingSlots.Logo, 3, () => throw new IOException("The file is in use."));

        Assert.Equal("Unexpected error: The file is in use.", vm.ErrorMessage);
        Assert.False(vm.CanSaveImages);
    }

    [Fact]
    public async Task A_picked_image_does_not_throw_away_what_was_typed_and_saving_the_texts_does_not_send_the_images()
    {
        var vm = await ShownAsync();
        vm.FormProductName = "PlanIt";
        await PickAsync(vm, BrandingSlots.LogoOnDark, 1, 2, 3);

        Assert.Equal("PlanIt", vm.FormProductName);
        Assert.True(vm.CanSave); // still unsaved

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["save-branding"], _api.BrandingCalls); // the picture waits for its own button
        Assert.True(Row(vm, BrandingSlots.LogoOnDark).IsPending);
        Assert.True(vm.CanSaveImages);

        await vm.SaveImagesCommand.ExecuteAsync(null);

        Assert.Equal(["save-branding", "upload-image:logo-on-dark"], _api.BrandingCalls);
        Assert.Equal("PlanIt", vm.FormProductName);
    }

    [Fact]
    public async Task Saving_the_images_does_not_throw_away_what_was_typed_meanwhile()
    {
        var vm = await ShownAsync();
        vm.FormProductName = "PlanIt";

        await SavedAsync(vm, BrandingSlots.LogoOnDark, 1, 2, 3);

        Assert.Equal("PlanIt", vm.FormProductName);
        Assert.True(vm.CanSave); // still unsaved
        Assert.Equal("kyuplan", _api.BrandingSettings.ProductName);
    }

    [Fact]
    public async Task An_image_can_be_removed_only_while_the_slot_shows_one_and_it_is_taken_away_when_the_images_are_saved()
    {
        var vm = await ShownAsync();
        var logo = Row(vm, BrandingSlots.Logo);
        var dark = Row(vm, BrandingSlots.LogoOnDark);
        Assert.True(vm.CanRemove(logo));
        Assert.False(vm.CanRemove(dark)); // nothing there: pressing it would change nothing

        vm.RemoveImageCommand.Execute(logo);

        var removed = Row(vm, BrandingSlots.Logo);
        Assert.False(removed.HasImage);
        Assert.True(removed.IsPending);
        Assert.False(vm.CanRemove(removed)); // it shows none now
        Assert.Empty(_api.BrandingCalls); // not yet
        Assert.False(vm.IsCustomized);
        Assert.True(vm.CanSaveImages);

        vm.RemoveImageCommand.Execute(dark); // not offered, so nothing happens
        Assert.False(Row(vm, BrandingSlots.LogoOnDark).IsPending);

        await vm.SaveImagesCommand.ExecuteAsync(null);

        Assert.Equal(["remove-image:logo"], _api.BrandingCalls);
        Assert.False(Row(vm, BrandingSlots.Logo).HasImage);
        Assert.False(Row(vm, BrandingSlots.Logo).IsPending);
        Assert.Equal("Images saved.", vm.NoticeText);
        Assert.True(vm.IsCustomized);
    }

    [Fact]
    public async Task Removing_a_picked_image_from_a_slot_that_has_none_saved_only_drops_it()
    {
        var vm = await ShownAsync();
        await PickAsync(vm, BrandingSlots.LogoOnDark, 1);
        Assert.True(vm.CanSaveImages);

        vm.RemoveImageCommand.Execute(Row(vm, BrandingSlots.LogoOnDark));

        Assert.False(Row(vm, BrandingSlots.LogoOnDark).HasImage);
        Assert.False(Row(vm, BrandingSlots.LogoOnDark).IsPending); // as it was: nothing to save
        Assert.False(vm.CanSaveImages);
    }

    [Fact]
    public async Task A_change_can_be_undone_which_shows_the_saved_image_again()
    {
        var vm = await ShownAsync();
        var saved = Row(vm, BrandingSlots.Logo).PreviewUrl;
        Assert.False(vm.CanUndo(Row(vm, BrandingSlots.Logo))); // nothing to undo

        await PickAsync(vm, BrandingSlots.Logo, 1);
        Assert.True(vm.UndoImageCommand.CanExecute(Row(vm, BrandingSlots.Logo)));
        vm.UndoImageCommand.Execute(Row(vm, BrandingSlots.Logo));

        Assert.Equal(saved, Row(vm, BrandingSlots.Logo).PreviewUrl);
        Assert.False(Row(vm, BrandingSlots.Logo).IsPending);
        Assert.False(vm.CanSaveImages);

        vm.RemoveImageCommand.Execute(Row(vm, BrandingSlots.Logo));
        vm.UndoImageCommand.Execute(Row(vm, BrandingSlots.Logo));

        Assert.Equal(saved, Row(vm, BrandingSlots.Logo).PreviewUrl);
        Assert.False(vm.CanSaveImages);
        Assert.Empty(_api.BrandingCalls);
    }

    [Fact]
    public async Task An_image_can_be_picked_once_the_page_is_loaded()
    {
        Assert.False(_vm.CanUpload); // nothing to pick for yet

        var vm = await ShownAsync();

        Assert.True(vm.CanUpload);
        Assert.Contains("1024 KB", vm.ImagesLede);
        Assert.Contains("until it is saved", vm.ImagesLede);
    }

    [Fact]
    public async Task The_remove_button_of_a_slot_that_shows_no_image_is_off()
    {
        var vm = await ShownAsync();

        Assert.True(vm.RemoveImageCommand.CanExecute(Row(vm, BrandingSlots.Logo)));
        Assert.False(vm.RemoveImageCommand.CanExecute(Row(vm, BrandingSlots.LogoOnDark)));
        Assert.False(vm.RemoveImageCommand.CanExecute(null)); // WPF asks before the row's button has its row

        vm.RemoveImageCommand.Execute(Row(vm, BrandingSlots.Logo));

        Assert.False(vm.RemoveImageCommand.CanExecute(Row(vm, BrandingSlots.Logo))); // it shows none now
    }

    // --- The colour: typed, or taken from the swatches ----------------------------------------------------

    [Fact]
    public async Task A_swatch_puts_its_colour_into_the_form_unless_the_form_has_it_already()
    {
        var vm = await ShownAsync();
        Assert.False(vm.PickAccentColorCommand.CanExecute("#2563EB")); // the saved colour, in another case
        Assert.True(vm.PickAccentColorCommand.CanExecute("#DC2626"));

        vm.PickAccentColorCommand.Execute("#DC2626");

        Assert.Equal("#DC2626", vm.FormAccentColor);
        Assert.True(vm.CanSave);
        Assert.False(vm.PickAccentColorCommand.CanExecute("#dc2626"));
        Assert.True(vm.PickAccentColorCommand.CanExecute("#2563EB")); // back to the saved one
    }

    [Fact]
    public void Every_swatch_is_a_colour_the_api_takes()
    {
        Assert.NotEmpty(BrandingAdminViewModel.AccentPresets);
        Assert.All(BrandingAdminViewModel.AccentPresets, color => Assert.True(BrandingRules.IsAccentColor(color)));
        Assert.Equal(BrandingAdminViewModel.AccentPresets.Count, BrandingAdminViewModel.AccentPresets.Distinct().Count());
    }

    // --- Back to the API's settings ------------------------------------------------------------------------

    [Fact]
    public async Task Going_back_is_off_until_something_was_saved_and_again_while_it_asks()
    {
        var vm = await ShownAsync();
        Assert.False(vm.AskResetCommand.CanExecute(null));

        await SavedAsync(vm, BrandingSlots.Logo, 1, 2, 3);
        Assert.True(vm.AskResetCommand.CanExecute(null));

        vm.AskResetCommand.Execute(null);
        Assert.False(vm.AskResetCommand.CanExecute(null)); // the question is open already

        vm.CancelResetCommand.Execute(null);
        Assert.True(vm.AskResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task Going_back_is_offered_only_once_something_was_saved_and_asks_first()
    {
        var vm = await ShownAsync();
        Assert.False(vm.CanReset);
        vm.AskResetCommand.Execute(null);
        Assert.False(vm.IsConfirmingReset);

        await SavedAsync(vm, BrandingSlots.Logo, 1, 2, 3);
        Assert.True(vm.CanReset);

        vm.AskResetCommand.Execute(null);
        Assert.True(vm.IsConfirmingReset);
        Assert.DoesNotContain("reset-branding", _api.BrandingCalls); // nothing happens before the answer

        vm.CancelResetCommand.Execute(null);
        Assert.False(vm.IsConfirmingReset);
        Assert.DoesNotContain("reset-branding", _api.BrandingCalls);
    }

    [Fact]
    public async Task Going_back_forgets_everything_and_shows_the_settings_of_the_api_again()
    {
        var vm = await ShownAsync();
        vm.FormProductName = "PlanIt";
        await vm.SaveCommand.ExecuteAsync(null);
        await SavedAsync(vm, BrandingSlots.LogoOnDark, 1, 2, 3);
        vm.FormOrganizationName = "typed but not saved";
        await PickAsync(vm, BrandingSlots.Icon, 4, 5, 6); // picked but not saved

        vm.AskResetCommand.Execute(null);
        await vm.ConfirmResetCommand.ExecuteAsync(null);

        Assert.Contains("reset-branding", _api.BrandingCalls);
        Assert.False(vm.IsConfirmingReset);
        Assert.False(vm.IsCustomized);
        Assert.False(vm.CanReset);
        Assert.Equal("kyuplan", vm.FormProductName);
        Assert.Equal("My Club", vm.FormOrganizationName); // what was typed is replaced by what the API says
        Assert.False(Row(vm, BrandingSlots.LogoOnDark).HasImage);
        Assert.All(vm.Images, row => Assert.False(row.IsPending)); // so are the pictures that were only picked
        Assert.False(vm.CanSaveImages);
        Assert.False(vm.CanSave);
        Assert.Equal("The settings of the API apply again.", vm.NoticeText);
    }

    [Fact]
    public async Task Confirming_without_having_asked_does_nothing()
    {
        var vm = await ShownAsync();
        await SavedAsync(vm, BrandingSlots.Logo, 1, 2, 3);

        await vm.ConfirmResetCommand.ExecuteAsync(null);

        Assert.DoesNotContain("reset-branding", _api.BrandingCalls);
        Assert.True(vm.IsCustomized);
    }

    // --- Language ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_texts_of_the_page_follow_the_language_without_asking_the_api_again()
    {
        var vm = await ShownAsync();
        vm.FormAccentColor = "nope";
        vm.RemoveImageCommand.Execute(Row(vm, BrandingSlots.Logo));
        await vm.SaveImagesCommand.ExecuteAsync(null);
        Assert.Equal("Images saved.", vm.NoticeText);

        _language.Select("de");

        Assert.Equal("Erscheinungsbild", vm.Title);
        Assert.Equal("Logo, heller Hintergrund", Row(vm, BrandingSlots.Logo).Title);
        Assert.Equal("Kein Logo: Die Clients zeigen nur den Namen.", Row(vm, BrandingSlots.Logo).EmptyText);
        Assert.Equal("Bilder gespeichert.", vm.NoticeText);
        Assert.Equal("Eine Farbe wie #2563eb oder #26e ist nötig.", vm.AccentColorProblem);
        Assert.Equal(1, _api.BrandingSettingsRequests);
    }

    // --- When the user is no administrator (any more) ------------------------------------------------------

    [Fact]
    public async Task When_the_rights_are_gone_nothing_of_the_page_is_kept()
    {
        var vm = await ShownAsync();
        Assert.True(vm.IsLoaded);

        _shell.Session.User = Dto(1, "admin", "admin", [], false);

        Assert.False(vm.CanView);
        Assert.False(vm.IsLoaded);
        Assert.Empty(vm.Images);
        Assert.Equal("", vm.FormProductName);
        Assert.False(vm.CanSave);
        Assert.False(vm.CanReset);
    }
}
