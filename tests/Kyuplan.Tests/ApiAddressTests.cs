using Kyuplan.Client.Api;
using Kyuplan.Client.ViewModels;

namespace Kyuplan.Tests;

public class ApiAddressTests
{
    [Theory]
    [InlineData("http://localhost:5100/", "http://localhost:5100/")]
    [InlineData("http://localhost:5100", "http://localhost:5100/")]
    [InlineData("  https://planner.example.org  ", "https://planner.example.org/")]
    [InlineData("HTTPS://Planner.Example.org/", "https://planner.example.org/")]
    [InlineData("https://example.org/planner", "https://example.org/planner/")] // the API may sit below a path
    [InlineData("http://192.168.1.20:30100/", "http://192.168.1.20:30100/")]
    public void A_usable_address_is_kept_in_one_canonical_form(string typed, string expected)
    {
        Assert.True(ApiAddress.TryNormalize(typed, out var address));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("planner.example.org")] // no scheme: http or https is not for us to guess
    [InlineData("localhost:5100")]
    [InlineData("ftp://planner.example.org/")]
    [InlineData("file:///C:/planner")]
    [InlineData("http://")]
    [InlineData("https://planner example.org/")]
    [InlineData("https://anna:secret@planner.example.org/")] // a login would end up in a settings file in clear text
    [InlineData("https://planner.example.org/?x=1")]
    [InlineData("https://planner.example.org/#top")]
    public void Anything_else_is_refused(string? typed)
    {
        Assert.False(ApiAddress.TryNormalize(typed, out var address));
        Assert.Equal("", address);
    }
}

public class ApiAddressViewModelTests
{
    [Fact]
    public void On_the_first_start_nothing_can_be_accepted_until_an_address_is_typed_and_nothing_is_criticised_before_that()
    {
        var vm = new ApiAddressViewModel();

        Assert.False(vm.AcceptCommand.CanExecute(null));
        Assert.False(vm.HasProblem);
        Assert.Null(vm.Normalized);
    }

    [Fact]
    public void An_unusable_address_is_reported_and_cannot_be_accepted()
    {
        var vm = new ApiAddressViewModel { Address = "planner.example.org" };

        Assert.True(vm.HasProblem);
        Assert.False(vm.AcceptCommand.CanExecute(null));
    }

    [Fact]
    public void A_usable_address_can_be_accepted_and_comes_out_in_its_canonical_form()
    {
        var vm = new ApiAddressViewModel();
        var raised = 0;
        vm.AcceptCommand.CanExecuteChanged += (_, _) => raised++;
        var accepted = new List<string?>();
        vm.Accepted += (_, _) => accepted.Add(vm.Normalized);

        vm.Address = " https://Planner.example.org ";

        Assert.False(vm.HasProblem);
        Assert.True(vm.AcceptCommand.CanExecute(null));
        Assert.True(raised > 0, "the button has to be told that it can be pressed now");

        vm.AcceptCommand.Execute(null);

        Assert.Equal(["https://planner.example.org/"], accepted);
    }

    [Fact]
    public void Fixing_a_typo_takes_the_problem_away_again()
    {
        var vm = new ApiAddressViewModel { Address = "planner" };
        Assert.True(vm.HasProblem);

        vm.Address = "http://planner/";

        Assert.False(vm.HasProblem);
    }

    [Fact]
    public void When_changing_the_address_the_current_one_is_the_start_and_is_not_worth_saving_again()
    {
        var vm = new ApiAddressViewModel("http://localhost:5100/");

        Assert.Equal("http://localhost:5100/", vm.Address);
        Assert.False(vm.AcceptCommand.CanExecute(null));

        vm.Address = "http://LOCALHOST:5100"; // the same address, spelled differently
        Assert.False(vm.AcceptCommand.CanExecute(null));

        vm.Address = "http://localhost:5101/";
        Assert.True(vm.AcceptCommand.CanExecute(null));
    }
}
