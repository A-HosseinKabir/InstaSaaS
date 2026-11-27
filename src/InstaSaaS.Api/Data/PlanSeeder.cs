using InstaSaaS.Domain;
using Microsoft.EntityFrameworkCore;

namespace InstaSaaS.Infrastructure;

// Inserts starter plans ONLY if the table is empty (never overwrites your own plans). Prices are in RIALS.
// Edit the numbers here (or change rows directly in the Plans table) before going live.
public static class PlanSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (await db.Plans.AnyAsync(ct)) return;
        db.Plans.AddRange(
            new Plan { Name = "پایه",    PriceRial = 1_500_000, DurationDays = 30, MaxInstagramAccounts = 1, MaxAutomations = 5 },
            new Plan { Name = "حرفه‌ای", PriceRial = 3_500_000, DurationDays = 30, MaxInstagramAccounts = 3, MaxAutomations = 25 },
            new Plan { Name = "سازمانی", PriceRial = 7_500_000, DurationDays = 30, MaxInstagramAccounts = 10, MaxAutomations = 100 });
        await db.SaveChangesAsync(ct);
    }
}
