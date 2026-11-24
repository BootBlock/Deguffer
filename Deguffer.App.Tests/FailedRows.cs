using System.Reflection;
using Deguffer.App.ViewModels;

namespace Deguffer.App.Tests;

/// <summary>
/// The rows of a settings form now stating a failed save, read from every <see cref="SettingSave"/> it
/// exposes, so a test asserts which rows say so and that every other row does not.
/// </summary>
internal static class FailedRows
{
    public static IReadOnlyList<string> Of(object form) =>
    [
        .. form.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(SettingSave))
            .Where(property => ((SettingSave)property.GetValue(form)!).Failed)
            .Select(property => property.Name),
    ];
}
