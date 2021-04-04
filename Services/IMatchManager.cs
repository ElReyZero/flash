﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using watchtower.Constants;
using watchtower.Models;

namespace watchtower.Services {

    public interface IMatchManager {

        /// <summary>
        /// Start a match. If the match is already running, nothing happens
        /// </summary>
        void Start();

        /// <summary>
        /// Restart an existing match. If a match is not running, nothing happens
        /// </summary>
        void Restart();

        /// <summary>
        /// Reset a match, clearing the runners
        /// </summary>
        void Reset();

        /// <summary>
        /// Pause a currently running match
        /// </summary>
        void Pause();

        /// <summary>
        /// Stop the current match. Does nothing if a match is already running
        /// </summary>
        void Stop();

        /// <summary>
        /// Get the current state of the match
        /// </summary>
        MatchState GetState();

        /// <summary>
        /// Set the settings used in a match
        /// </summary>
        /// <param name="settings">Settings to use in the match</param>
        void SetSettings(MatchSettings settings);

        /// <summary>
        /// Get the current settings in a match
        /// </summary>
        /// <returns></returns>
        MatchSettings GetSettings();

        /// <summary>
        /// Add a new character to a runner. If no runner at the index has been set, a new runner is created
        /// </summary>
        /// <param name="index">Index of the runner to add the character to</param>
        /// <param name="charName">Name of the character to add. Case insensitive</param>
        /// <returns>If the character was successfully added or not</returns>
        Task<bool> AddCharacter(int index, string charName);

        void RemoveCharacter(int index, string charName);

        /// <summary>
        /// Set the name of a runner
        /// </summary>
        /// <param name="index">Index of the runner to set</param>
        /// <param name="playerName">Name of the runner to set</param>
        void SetRunnerName(int index, string? playerName);

        /// <summary>
        /// Set the score of a runner
        /// </summary>
        /// <param name="index">Index of the runner to set the score of</param>
        /// <param name="score">Score to set the runner to</param>
        void SetScore(int index, int score);

        /// <summary>
        /// Get the score of a runner
        /// </summary>
        /// <param name="index">Index of the runner to get the score of</param>
        int GetScore(int index);

        /// <summary>
        /// Get the runner being tracked
        /// </summary>
        /// <param name="index">Index of the runner to get</param>
        TrackedPlayer? GetPlayer(int index);

        /// <summary>
        /// Get all runners in this match
        /// </summary>
        /// <returns>The list of runners</returns>
        List<TrackedPlayer> GetPlayers();

        /// <summary>
        /// Get the <c>DateTime</c> of when a match was started
        /// </summary>
        DateTime GetMatchStart();

        /// <summary>
        /// Get the <c>DateTime</c> of when the match ended, or <c>null</c> if it hasn't ended
        /// </summary>
        DateTime? GetMatchEnd();

        /// <summary>
        /// Get how many seconds a match has been running for. Not really useful if the match has not started
        /// </summary>
        int GetMatchLength();

    }

}
