﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace watchtower.Models {

    /// <summary>
    /// Settings used for the auto challenge system
    /// </summary>
    public class AutoChallengeSettings {

        /// <summary>
        /// Are auto challenges enabled?
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How many seconds before the first challenge poll will be ran
        /// </summary>
        public int StartDelay { get; set; } = 120;

        /// <summary>
        /// How many seconds between a challenge poll being started
        /// </summary>
        public int Interval { get; set; } = 300;

        /// <summary>
        /// How many options will be given in a poll
        /// </summary>
        public int OptionCount { get; set; } = 3;

        /// <summary>
        /// How many seconds users can vote
        /// </summary>
        public int PollTime { get; set; } = 60;

        /// <summary>
        /// If all challenges will end when a new one starts
        /// </summary>
        public bool EndPrevious { get; set; } = true;

    }
}
