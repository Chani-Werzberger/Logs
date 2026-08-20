using LogsPlatform.Web.Services;
using Xunit;

namespace LogsPlatform.Tests.Web;

public class ExceptionFingerprinterTests
{
    private const string StackTraceA =
        "   at MyApp.Payments.PaymentGateway.AuthorizeCard(String cardNumber) in C:\\src\\PaymentGateway.cs:line 42\n" +
        "   at MyApp.Payments.ProcessPayment(Order order) in C:\\src\\ProcessPayment.cs:line 18";

    private const string StackTraceALaterBuild =
        "   at MyApp.Payments.PaymentGateway.AuthorizeCard(String cardNumber) in C:\\src\\PaymentGateway.cs:line 51\n" +
        "   at MyApp.Payments.ProcessPayment(Order order) in C:\\src\\ProcessPayment.cs:line 25";

    private const string StackTraceB =
        "   at MyApp.Inventory.StockManager.ReserveStock(String sku) in C:\\src\\StockManager.cs:line 10";

    private const string StackTraceADifferentMachine =
        "   at MyApp.Payments.PaymentGateway.AuthorizeCard(String cardNumber) in D:\\build\\agent1\\src\\PaymentGateway.cs:line 42\n" +
        "   at MyApp.Payments.ProcessPayment(Order order) in /home/ci/src/ProcessPayment.cs:line 18";

    [Fact]
    public void Compute_SameInputs_ProducesSameFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DifferentExceptionType_ProducesDifferentFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.InvalidOperationException", StackTraceA, "template");
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_StackTraceLineNumbersDiffer_SameFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceALaterBuild, "template");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_StackTraceSourcePathsDiffer_SameFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceADifferentMachine, "template");
        Assert.Equal(first, second);
    }

    [Fact]
    public void Compute_DifferentStackTrace_ProducesDifferentFingerprint()
    {
        var first = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceA, "template");
        var second = ExceptionFingerprinter.Compute("System.TimeoutException", StackTraceB, "template");
        Assert.NotEqual(first, second);
    }
}
