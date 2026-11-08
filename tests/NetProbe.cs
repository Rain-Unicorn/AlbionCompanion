using System;
using AlbionCompanion.Core;

static class NetProbe
{
    static void Main()
    {
        try
        {
            var quotes = MarketClient.Fetch(
                new string[] { "T4_BAG", "T5_2H_BOW@2" },
                new string[] { "Caerleon", "Bridgewatch" },
                15);
            Console.WriteLine("quotes=" + quotes.Count);
            foreach (var q in quotes)
                Console.WriteLine(q.ItemId + " @ " + q.City +
                    "  sellMin=" + q.SellMin + " buyMax=" + q.BuyMax +
                    "  updated=" + q.SellMinDate);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message);
            Environment.Exit(1);
        }
    }
}
