using System.Text.RegularExpressions;

namespace PMS.Application.Helpers;

/// <summary>
/// Vietnamese license plates (Circular 24/2023/TT-BCA), validated after removing
/// separators (space, '-', '.') and upper-casing:
///   Car        : 51A-123.45, 29A-1234, 51LD-123.45
///   Motorcycle : 29-B1 123.45, 59-X1 1234, 29-AA 123.45, 29-MĐ1 123.45
/// Province code 11-99, then a 1-2 letter series, then 4-6 digits
/// (an optional series digit followed by a 4 or 5 digit number).
/// </summary>
public static class LicensePlateHelper
{
    private static readonly Regex AllowedCharacters = new(@"^[0-9A-ZĐ]+([ .\-][0-9A-ZĐ]+)*$", RegexOptions.Compiled);

    private static readonly Regex PlatePattern = new(@"^(1[1-9]|[2-9][0-9])[A-ZĐ]{1,2}[0-9]{4,6}$", RegexOptions.Compiled);

    public static bool IsValidVietnamesePlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return false;

        var upper = plate.Trim().ToUpperInvariant();

        return AllowedCharacters.IsMatch(upper) && PlatePattern.IsMatch(Regex.Replace(upper, @"[ .\-]", string.Empty));
    }
}
