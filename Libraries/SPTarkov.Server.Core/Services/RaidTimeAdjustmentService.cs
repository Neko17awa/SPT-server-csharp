using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Constants;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Game;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Location;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;

namespace SPTarkov.Server.Core.Services;

[Injectable(InjectionType.Singleton)]
public class RaidTimeAdjustmentService(
    ISptLogger<RaidTimeAdjustmentService> logger,
    DatabaseService databaseService,
    RandomUtil randomUtil,
    WeightedRandomHelper weightedRandomHelper,
    ProfileActivityService profileActivityService,
    ConfigServer configServer
)
{
    protected readonly LocationConfig LocationConfig = configServer.GetConfig<LocationConfig>();

    /// <summary>
    ///     Make alterations to the base map data passed in
    ///     Loot multipliers/waves/wave start times
    /// </summary>
    /// <param name="raidAdjustments">Changes to process on map</param>
    /// <param name="mapBase">Map to adjust</param>
    public void MakeAdjustmentsToMap(RaidChanges raidAdjustments, LocationBase mapBase)
    {
        if (raidAdjustments.DynamicLootPercent < 100 || raidAdjustments.StaticLootPercent < 100)
        {
            logger.Debug(
                $"Adjusting dynamic loot multipliers to {raidAdjustments.DynamicLootPercent}% and static loot multipliers to {raidAdjustments.StaticLootPercent}% of original"
            );
        }

        // Change loot multiplier values before they're used below
        if (raidAdjustments.DynamicLootPercent < 100)
        {
            AdjustLootMultipliers(LocationConfig.LooseLootMultiplier, raidAdjustments.DynamicLootPercent);
        }

        if (raidAdjustments.StaticLootPercent < 100)
        {
            AdjustLootMultipliers(LocationConfig.StaticLootMultiplier, raidAdjustments.StaticLootPercent);
        }

        // Adjust the escape time limit
        mapBase.EscapeTimeLimit = raidAdjustments.RaidTimeMinutes;

        // Adjust map exits
        foreach (var exitChange in raidAdjustments.ExitChanges)
        {
            var exitToChange = mapBase.Exits.FirstOrDefault(exit => exit.Name == exitChange.Name);
            if (exitToChange is null)
            {
                logger.Debug($"Exit with Id: {exitChange.Name} not found, skipping");

                return;
            }

            if (exitChange.Chance is not null)
            {
                exitToChange.Chance = exitChange.Chance;
            }

            if (exitChange.MinTime is not null)
            {
                exitToChange.MinTime = exitChange.MinTime;
            }

            if (exitChange.MaxTime is not null)
            {
                exitToChange.MaxTime = exitChange.MaxTime;
            }
        }

        // Make alterations to bot spawn waves now player is simulated spawning later
        var mapSettings = GetMapSettings(mapBase.Id);
        if (mapSettings.AdjustWaves)
        {
            AdjustWaves(mapBase, raidAdjustments);

            AdjustPMCSpawns(mapBase, raidAdjustments);
        }
    }

    /// <summary>
    ///     Adjust the loot multiplier values passed in to be a % of their original value
    /// </summary>
    /// <param name="mapLootMultipliers">Multipliers to adjust</param>
    /// <param name="loosePercent">Percent to change values to</param>
    protected void AdjustLootMultipliers(Dictionary<string, double> mapLootMultipliers, double? loosePercent)
    {
        foreach (var location in mapLootMultipliers)
        {
            mapLootMultipliers[location.Key] = randomUtil.GetPercentOfValue(mapLootMultipliers[location.Key], loosePercent ?? 1);
        }
    }

    /// <summary>
    ///     Adjust bot waves to act as if player spawned later
    /// </summary>
    /// <param name="mapBase">Map to adjust</param>
    /// <param name="raidAdjustments">Map adjustments</param>
    protected void AdjustWaves(LocationBase mapBase, RaidChanges raidAdjustments)
    {
        // Remove waves that spawned before the player joined
        var originalWaveCount = mapBase.Waves.Count;
        mapBase.Waves = mapBase.Waves.Where(wave => wave.TimeMax > raidAdjustments.SimulatedRaidStartSeconds).ToList();

        // Adjust wave min/max times to match new simulated start
        var startSeconds = raidAdjustments.SimulatedRaidStartSeconds.GetValueOrDefault(1);
        foreach (var wave in mapBase.Waves)
        {
            // Don't let time fall below 0
            wave.TimeMin -= (int)Math.Max(startSeconds, 0);
            wave.TimeMax -= (int)Math.Max(startSeconds, 0);
        }

        logger.Debug(
            $"Removed: {originalWaveCount - mapBase.Waves.Count} wave from map due to simulated raid start time of {raidAdjustments.SimulatedRaidStartSeconds / 60} minutes"
        );
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="mapBase">Map to adjust</param>
    /// <param name="raidAdjustments">Map adjustments</param>
    protected void AdjustPMCSpawns(LocationBase mapBase, RaidChanges raidAdjustments)
    {
        var originalPmcWaveCount = mapBase.BossLocationSpawn.Count;

        // Filter PMCs by spawn time but allow all normal boss types (e.g. Tagilla/Killa)
        mapBase.BossLocationSpawn = mapBase
            .BossLocationSpawn.Where(boss =>
                boss.Time > raidAdjustments.SimulatedRaidStartSeconds // Spawns after simulated player start
                || (
                    !string.Equals(boss.BossName, "pmcusec", StringComparison.OrdinalIgnoreCase) // or
                    && !string.Equals(boss.BossName, "pmcbear", StringComparison.OrdinalIgnoreCase) // isn't a pmc
                )
            )
            .ToList();

        // Adjust wave min/max times to match new simulated start
        var startSeconds = raidAdjustments.SimulatedRaidStartSeconds.GetValueOrDefault(1);
        foreach (var wave in mapBase.Waves)
        {
            // Don't let time fall below 0
            wave.TimeMin -= (int)Math.Max(startSeconds, 0);
            wave.TimeMax -= (int)Math.Max(startSeconds, 0);
        }

        // Now additionally move all PMCs back so they spawn starting at the beginning of the raid
        var pmcSpawns = mapBase.BossLocationSpawn.Where(boss => boss.BossName is Sides.PmcUsec or Sides.PmcBear);
        var firstPmcSpawn = pmcSpawns.OrderBy(boss => boss.Time).FirstOrDefault();
        if (firstPmcSpawn != null)
        {
            var pmcStartSeconds = firstPmcSpawn.Time.GetValueOrDefault(1);
            foreach (var spawn in pmcSpawns)
            {
                // Sanity check, the client won't spawn a time of 0
                spawn.Time = (double)Math.Max(spawn.Time.GetValueOrDefault(1) - pmcStartSeconds, 1);
            }

            logger.Debug($"Offset PMC spawns by {pmcStartSeconds} seconds");
        }

        logger.Debug(
            $"Removed: {originalPmcWaveCount - mapBase.BossLocationSpawn.Count} boss waves from map due to simulated raid start time of {raidAdjustments.SimulatedRaidStartSeconds / 60} minutes"
        );
    }

    /// <summary>
    ///     Create a randomised adjustment to the raid based on map data in location.json
    /// </summary>
    /// <param name="sessionId">Session id</param>
    /// <param name="request">Raid adjustment request</param>
    /// <returns>Response to send to client</returns>
    public RaidChanges GetRaidAdjustments(MongoId sessionId, GetRaidTimeRequest request)
    {
        var globals = databaseService.GetGlobals();
        var mapBase = databaseService.GetLocation(request.Location.ToLowerInvariant()).Base;
        var baseEscapeTimeMinutes = mapBase.EscapeTimeLimit;

        // Prep result object to return
        var result = new RaidChanges
        {
            NewSurviveTimeSeconds = globals.Configuration.Exp.MatchEnd.SurvivedSecondsRequirement,
            OriginalSurvivalTimeSeconds = globals.Configuration.Exp.MatchEnd.SurvivedSecondsRequirement,
            DynamicLootPercent = 100,
            StaticLootPercent = 100,
            SimulatedRaidStartSeconds = 0,
            RaidTimeMinutes = baseEscapeTimeMinutes,
            ExitChanges = [],
        };

        // Pmc raid, send default
        if (string.Equals(request.Side, "pmc", StringComparison.OrdinalIgnoreCase))
        {
            return result;
        }

        // We're scav, adjust values
        var mapSettings = GetMapSettings(request.Location);

        // Chance of reducing raid time for scav, not guaranteed
        if (!randomUtil.GetChance100(mapSettings.ReducedChancePercent))
        // Send default
        {
            return result;
        }

        // Get the weighted percent to reduce the raid time by
        var chosenRaidReductionPercent = int.Parse(weightedRandomHelper.GetWeightedValue(mapSettings.ReductionPercentWeights));
        var raidTimeRemainingPercent = 100 - chosenRaidReductionPercent;

        // How many minutes raid will last
        var newRaidTimeMinutes = Math.Floor(randomUtil.ReduceValueByPercent(baseEscapeTimeMinutes ?? 1, chosenRaidReductionPercent));

        // Time player spawns into the raid if it was online
        var simulatedRaidStartTimeMinutes = baseEscapeTimeMinutes - newRaidTimeMinutes;
        result.SimulatedRaidStartSeconds = simulatedRaidStartTimeMinutes * 60;
        result.RaidTimeMinutes = newRaidTimeMinutes;

        // Calculate how long player needs to be in raid to get a `survived` extract status
        result.NewSurviveTimeSeconds = Math.Max(
            result.OriginalSurvivalTimeSeconds.Value - (baseEscapeTimeMinutes.Value - newRaidTimeMinutes) * 60,
            0
        );

        if (mapSettings.ReduceLootByPercent)
        {
            result.DynamicLootPercent = Math.Max(raidTimeRemainingPercent, mapSettings.MinDynamicLootPercent);
            result.StaticLootPercent = Math.Max(raidTimeRemainingPercent, mapSettings.MinStaticLootPercent);
        }

        logger.Debug($"Reduced: {request.Location} raid time by: {chosenRaidReductionPercent}% to {newRaidTimeMinutes} minutes");

        // Calculate how long player needs to be in raid to get a `survived` extract status
        result.NewSurviveTimeSeconds = Math.Max(
            result.OriginalSurvivalTimeSeconds - (baseEscapeTimeMinutes - newRaidTimeMinutes) * 60 ?? 0,
            0D
        );

        var exitAdjustments = GetExitAdjustments(mapBase, newRaidTimeMinutes);
        if (exitAdjustments.Any())
        {
            result.ExitChanges.AddRange(exitAdjustments);
        }

        // Store state to use in loot generation
        profileActivityService.GetProfileActivityRaidData(sessionId).RaidAdjustments = result;

        return result;
    }

    /// <summary>
    ///     Get raid start time settings for specific map
    /// </summary>
    /// <param name="location">Map Location e.g. bigmap</param>
    /// <returns>ScavRaidTimeLocationSettings</returns>
    protected ScavRaidTimeLocationSettings GetMapSettings(string location)
    {
        var mapSettings = LocationConfig.ScavRaidTimeSettings.Maps[location.ToLowerInvariant()];
        if (mapSettings is null)
        {
            logger.Warning($"Unable to find scav raid time settings for map: {location}, using defaults");
            return new ScavRaidTimeLocationSettings();
        }

        return mapSettings;
    }

    /// <summary>
    ///     Adjust exit times to handle scavs entering raids part-way through
    /// </summary>
    /// <param name="mapBase">Map base file player is on</param>
    /// <param name="newRaidTimeMinutes">How long raid is in minutes</param>
    /// <returns>List of exit changes to send to client</returns>
    protected List<ExtractChange> GetExitAdjustments(LocationBase mapBase, double newRaidTimeMinutes)
    {
        List<ExtractChange> result = [];
        // Adjust train exits only
        foreach (var exit in mapBase.Exits)
        {
            if (exit.PassageRequirement != RequirementState.Train)
            {
                continue;
            }

            // Prepare train adjustment object
            var exitChange = new ExtractChange
            {
                Name = exit.Name,
                MinTime = null,
                MaxTime = null,
                Chance = null,
            };

            // At what minute we simulate the player joining the raid
            var simulatedRaidEntryTimeMinutes = mapBase.EscapeTimeLimit - newRaidTimeMinutes;

            // How many seconds have elapsed in the raid when the player joins
            var reductionSeconds = simulatedRaidEntryTimeMinutes * 60;

            // Delay between the train extract activating and it becoming available to board
            //
            // Test method for determining this value:
            // 1) Set MinTime, MaxTime, and Count for the train extract all to 120
            // 2) Load into Reserve or Lighthouse as a PMC (both have the same result)
            // 3) Board the train when it arrives
            // 4) Check the raid time on the Raid Ended Screen (it should always be the same)
            //
            // trainArrivalDelaySeconds = [raid time on raid-ended screen] - MaxTime - Count - ExfiltrationTime
            // Example: Raid Time = 5:33 = 333 seconds
            //          trainArrivalDelaySeconds = 333 - 120 - 120 - 5 = 88
            //
            // I added 2 seconds just to be safe...
            //
            var trainArrivalDelaySeconds = LocationConfig.ScavRaidTimeSettings.Settings.TrainArrivalDelayObservedSeconds;

            // Determine the earliest possible time in the raid when the train would leave
            var earliestPossibleDepartureMinutes = (exit.MinTime + exit.Count + exit.ExfiltrationTime + trainArrivalDelaySeconds) / 60;

            // If raid is after last moment train can leave, assume train has already left, disable extract
            var mostPossibleTimeRemainingAfterDeparture = mapBase.EscapeTimeLimit - earliestPossibleDepartureMinutes;
            if (newRaidTimeMinutes < mostPossibleTimeRemainingAfterDeparture)
            {
                exitChange.Chance = 0;

                logger.Debug(
                    $"Train Exit: {exit.Name} disabled as new raid time {newRaidTimeMinutes} minutes is below {mostPossibleTimeRemainingAfterDeparture} minutes"
                );

                result.Add(exitChange);

                continue;
            }

            // Reduce extract arrival times. Negative values seem to make extract turn red in game.
            exitChange.MinTime = Math.Max(exit.MinTime - reductionSeconds ?? 0, 0);
            exitChange.MaxTime = Math.Max(exit.MaxTime - reductionSeconds ?? 0, 0);

            logger.Debug($"Train appears between: {exitChange.MinTime} and {exitChange.MaxTime} seconds raid time");

            result.Add(exitChange);
        }

        return result;
    }
}
