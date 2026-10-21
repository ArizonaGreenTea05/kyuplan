namespace Kyuplan.Contracts;

/// <summary>Which spreadsheet of an exam the API hands out (<c>/api/exams/{id}/payments/export</c>, <c>/api/exams/{id}/results/export</c>).</summary>
public enum ExamExportKind
{
    Payments,
    Results,
}
