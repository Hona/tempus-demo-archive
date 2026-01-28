using Microsoft.EntityFrameworkCore;
using TempusDemoArchive.Persistence.Models.STVs;

namespace TempusDemoArchive.Jobs;

internal static class WrHistoryChatQueries
{
    public static IQueryable<StvChat> WhereLikelyTempusWrMessage(this IQueryable<StvChat> query)
    {
        return query
            .Where(chat => EF.Functions.Like(chat.Text, "Tempus | (%"))
            .Where(chat => (EF.Functions.Like(chat.Text, "%map run%")
                            && EF.Functions.Like(chat.Text, "%WR%"))
                           || EF.Functions.Like(chat.Text, "%beat the map record%")
                           || EF.Functions.Like(chat.Text, "%set the first map record%")
                           || EF.Functions.Like(chat.Text, "%is ranked%with time%")
                           || EF.Functions.Like(chat.Text, "%set Bonus%")
                           || EF.Functions.Like(chat.Text, "%set Course%")
                           || EF.Functions.Like(chat.Text, "%set C%")
                           || EF.Functions.Like(chat.Text, "%broke%Bonus%")
                           || EF.Functions.Like(chat.Text, "%broke%Course%")
                           || EF.Functions.Like(chat.Text, "%broke C%")
                           || EF.Functions.Like(chat.Text, "% WR)%"));
    }

    public static IQueryable<StvChat> WhereLikelyIrcWrMessage(this IQueryable<StvChat> query)
    {
        return query
            .Where(chat => EF.Functions.Like(chat.Text, ":: Tempus -%")
                           || EF.Functions.Like(chat.Text, ":: (%"))
            .Where(chat => chat.Text.Contains(" WR: "))
            .Where(chat => chat.Text.Contains(" broke ") || chat.Text.Contains(" set "));
    }
}
