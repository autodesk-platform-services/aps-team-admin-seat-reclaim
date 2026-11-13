using System.Text.RegularExpressions;

/// <summary>
/// Joins Team Admin assignments with usage data. A (user, product) assignment is inactive when the user's email
/// (from Team Admin list team users, which also gives the userId used to remove the assignment) has no usage of
/// the matching product on or after <c>asOf - days</c>, including users with no usage at all.
/// </summary>
public static partial class InactiveUserAnalyzer
{
    public static InactiveUsersResult Analyze(
        IEnumerable<TeamUser> users,
        IEnumerable<ProductPool> pools,
        IEnumerable<Assignment> assignments,
        IEnumerable<UsageRecord> usage,
        DateOnly asOf,
        int days,
        string source,
        IDictionary<string, string>? productMap = null,
        IEnumerable<string>? warnings = null)
    {
        var usersById = users.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First());
        var usageList = usage.ToList();
        var usageProducts = usageList.Select(u => u.Product).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        var cutoff = asOf.AddDays(-days);
        var allWarnings = warnings?.ToList() ?? [];

        // Latest use per (email, usage product)
        var lastUsed = usageList
            .GroupBy(u => (Email: u.Email.ToLowerInvariant(), Product: u.Product.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => g.Max(u => u.LastUsed));

        var assignmentList = assignments.ToList();
        var poolNames = pools
            .Select(p => (p.ProductId, p.ProductName))
            .Concat(assignmentList.Select(a => (a.ProductId, a.ProductName)))
            .GroupBy(p => p.ProductId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ProductName).FirstOrDefault(n => !string.IsNullOrEmpty(n)));

        var matches = poolNames
            .Select(p => MatchProduct(p.Key, p.Value, usageProducts, productMap))
            .OrderBy(m => m.ProductName)
            .ToList();
        var matchByProduct = matches.ToDictionary(m => m.ProductId);

        var unmatched = matches.Where(m => m.UsageProduct == null && assignmentList.Any(a => a.ProductId == m.ProductId)).ToList();
        if (unmatched.Count > 0)
        {
            allWarnings.Add("No usage data matched these products, so all their users are listed as inactive. " +
                "Check the product mapping if this is unexpected: " + string.Join(", ", unmatched.Select(m => m.ProductName ?? m.ProductId)));
        }

        var rows = new List<InactiveRow>();
        foreach (var a in assignmentList)
        {
            usersById.TryGetValue(a.UserId, out var user);
            var email = a.Email ?? user?.Email;
            var usageProduct = matchByProduct.TryGetValue(a.ProductId, out var m) ? m.UsageProduct : null;
            DateOnly? last = null;
            if (email != null && usageProduct != null
                && lastUsed.TryGetValue((email.ToLowerInvariant(), usageProduct.ToLowerInvariant()), out var date))
            {
                last = date;
            }
            if (last >= cutoff)
            {
                continue; // active in the window
            }
            var name = user == null ? null : $"{user.FirstName} {user.LastName}".Trim();
            rows.Add(new InactiveRow(
                a.UserId,
                string.IsNullOrEmpty(name) ? null : name,
                email,
                a.ProductId,
                poolNames.GetValueOrDefault(a.ProductId) ?? a.ProductName,
                a.AssignmentType,
                Removable: string.Equals(a.AssignmentType, "DIRECT", StringComparison.OrdinalIgnoreCase),
                last,
                last.HasValue ? asOf.DayNumber - last.Value.DayNumber : null));
        }

        rows = [.. rows.OrderBy(r => r.Name ?? r.Email).ThenBy(r => r.ProductName)];
        return new InactiveUsersResult(asOf, cutoff, days, source, rows, matches, usageProducts, allWarnings);
    }

    private static ProductMatch MatchProduct(string productId, string? productName, List<string> usageProducts, IDictionary<string, string>? productMap)
    {
        if (productMap != null && productMap.TryGetValue(productId, out var manual) && !string.IsNullOrWhiteSpace(manual))
        {
            return new ProductMatch(productId, productName, manual, Manual: true);
        }
        if (string.IsNullOrWhiteSpace(productName))
        {
            return new ProductMatch(productId, productName, null, Manual: false);
        }
        var target = NormalizeProductName(productName);
        var exact = usageProducts.FirstOrDefault(u => NormalizeProductName(u) == target);
        // Fall back to containment (e.g. "AutoCAD" vs "AutoCAD - including specialized toolsets"), longest name first.
        var partial = exact ?? usageProducts
            .Where(u => NormalizeProductName(u) is { Length: > 2 } n && (n.Contains(target) || target.Contains(n)))
            .OrderByDescending(u => u.Length)
            .FirstOrDefault();
        return new ProductMatch(productId, productName, partial, Manual: false);
    }

    /// <summary>Lowercase, drop "subscription"/"including ..." suffixes and punctuation.</summary>
    public static string NormalizeProductName(string name)
    {
        var n = name.ToLowerInvariant();
        var including = n.IndexOf("including", StringComparison.Ordinal);
        if (including > 0)
        {
            n = n[..including];
        }
        n = n.Replace("subscription", " ");
        n = NonAlphanumeric().Replace(n, " ");
        return Whitespace().Replace(n, " ").Trim();
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
