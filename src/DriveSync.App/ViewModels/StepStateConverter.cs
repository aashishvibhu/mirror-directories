using System.Globalization;
using System.Windows.Data;

namespace DriveSync.App.ViewModels;

/// <summary>Compares a wizard step's number with the current step, for the step list.</summary>
public sealed class StepStateConverter : IMultiValueConverter
{
    readonly Func<int, int, bool> _test;

    StepStateConverter(Func<int, int, bool> test) => _test = test;

    public static StepStateConverter IsCurrent { get; } = new((number, step) => number == step);

    public static StepStateConverter IsDone { get; } = new((number, step) => number < step);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [int number, int step] && _test(number, step);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
