using System;
using System.Collections.Generic;

namespace SyncMaster.Core;

public static class MonthNames
{
    private static readonly IReadOnlyList<string> _all = new[]
    {
        "January", "February", "March",    "April",
        "May",     "June",     "July",     "August",
        "September", "October", "November", "December",
    };

    public static IReadOnlyList<string> All => _all;

    public static string Get(int month)
    {
        if (month < 1 || month > 12)
            throw new ArgumentOutOfRangeException(nameof(month), month,
                "Month must be between 1 and 12.");
        return _all[month - 1];
    }
}
