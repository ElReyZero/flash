﻿using DSharpPlus.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using watchtower.Census;
using watchtower.Code;
using watchtower.Code.Census.Implementations;
using watchtower.Code.Challenge;
using watchtower.Code.Constants;
using watchtower.Constants;
using watchtower.Models;
using watchtower.Models.Census;
using watchtower.Models.Events;
using watchtower.Realtime;
using watchtower.Services.Queue;

namespace watchtower.Services {

    public class MatchManager : IMatchManager {

        const double TICKS_PER_SECOND = 10000000D;

        private readonly ILogger<MatchManager> _Logger;

        private readonly ICharacterCollection _CharacterColleciton;
        private readonly IItemCollection _ItemCollection;
        private readonly ExperienceCollection _ExpCollection;

        private readonly IRealtimeMonitor _Realtime;
        private readonly IRealtimeEventBroadcastService _RealtimeEvents;
        private readonly IMatchEventBroadcastService _MatchEvents;
        private readonly IMatchMessageBroadcastService _MatchMessages;
        private readonly IAdminMessageBroadcastService _AdminMessages;
        private readonly IChallengeManager _Challenges;
        private readonly IChallengeEventBroadcastService _ChallengeEvents;
        private readonly ISecondTimer _Timer;

        private readonly DiscordMessageQueue _DiscordMessageQueue;
        private readonly DiscordThreadManager _ThreadManager;

        private RoundState _RoundState = RoundState.UNSTARTED;
        private MatchState _MatchState = MatchState.UNSTARTED;
        private DateTime _MatchStart = DateTime.UtcNow;
        private DateTime? _MatchEnd = null;
        private long _MatchTicks = 0;

        private readonly Dictionary<int, TrackedPlayer> _Players = new Dictionary<int, TrackedPlayer>();
        private MatchSettings _Settings = new MatchSettings();
        private AutoChallengeSettings _AutoSettings = new AutoChallengeSettings();

        public MatchManager(ILogger<MatchManager> logger,
                ICharacterCollection charColl, IItemCollection itemColl,
                IRealtimeEventBroadcastService events, IMatchEventBroadcastService matchEvents,
                IRealtimeMonitor realtime, IChallengeEventBroadcastService challengeEvents,
                IMatchMessageBroadcastService matchMessages, IAdminMessageBroadcastService adminMessages,
                IChallengeManager challenges, ISecondTimer timer,
                ExperienceCollection expColl, DiscordMessageQueue queue,
                DiscordThreadManager threadManager) {

            _Logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _CharacterColleciton = charColl ?? throw new ArgumentNullException(nameof(charColl));
            _ItemCollection = itemColl ?? throw new ArgumentNullException(nameof(itemColl));
            _ExpCollection = expColl ?? throw new ArgumentNullException(nameof(expColl));

            _Realtime = realtime ?? throw new ArgumentNullException(nameof(realtime));
            _RealtimeEvents = events ?? throw new ArgumentNullException(nameof(events));
            _MatchEvents = matchEvents ?? throw new ArgumentNullException(nameof(matchEvents));
            _ChallengeEvents = challengeEvents ?? throw new ArgumentNullException(nameof(challengeEvents));

            _MatchMessages = matchMessages ?? throw new ArgumentNullException(nameof(matchMessages));
            _AdminMessages = adminMessages ?? throw new ArgumentNullException(nameof(adminMessages));
            _Challenges = challenges ?? throw new ArgumentNullException(nameof(challenges));

            _Timer = timer ?? throw new ArgumentNullException(nameof(timer));

            _DiscordMessageQueue = queue ?? throw new ArgumentNullException(nameof(queue));

            SetSettings(new MatchSettings());

            AddListeners();
            _ThreadManager = threadManager;
        }

        private void AddListeners() {
            _RealtimeEvents.OnKillEvent += KillHandler;
            _RealtimeEvents.OnExpEvent += ExpHandler;

            _Timer.OnTick += OnTick;
        }

        public async Task<bool> AddCharacter(int index, string charName) {
            if (_MatchState != MatchState.STARTED) {
                return false;
            }

            if (_Players.TryGetValue(index, out TrackedPlayer? player) == false) {
                player = new TrackedPlayer {
                    Index = index,
                    RunnerName = $"Runner {index + 1}"
                };

                _Players.Add(index, player);

                _AdminMessages.Log($"Created team {player.Index}:{player.RunnerName}");
            }

            foreach (Character c in player.Characters) {
                if (c.Name.ToLower() == charName.ToLower()) {
                    _Logger.LogWarning($"Not adding duplicate players {charName}");
                    return true;
                }
            }

            Character? ch = await _CharacterColleciton.GetByNameAsync(charName);
            if (ch == null) {
                _Logger.LogWarning($"Failed to add character {charName} to Runner {index}, does not exist");
                return false;
            }

            if (player.RunnerName == $"Runner {index + 1}") {
                _AdminMessages.Log($"Renamed team {index}:{player.RunnerName} to {ch.Name}");
                player.RunnerName = ch.Name;
            }

            player.Characters.Add(ch);

            _Realtime.Subscribe(new Subscription() {
                Characters = { ch.ID },
                Events = { 
                    "Death",
                    "GainExperience" 
                }
            });

            string s = $"Team {index}:{player.RunnerName} added character {charName}";
            _AdminMessages.Log(s);
            await _ThreadManager.SendThreadMessage(s);

            _MatchEvents.EmitPlayerUpdateEvent(index, player);

            return true;
        }

        public void RemoveCharacter(int index, string charName) {
            if (_Players.TryGetValue(index, out TrackedPlayer? player) == true) {
                player.Characters = player.Characters.Where(iter => iter.Name.ToLower() != charName.ToLower()).ToList();
                _AdminMessages.Log($"Team {index}:{player.RunnerName} removed character {charName}");
            } else {
                _Logger.LogWarning($"Cannot remove {charName} from player {index} cause it wasn't found");
            }
        }

        public void SetSettings(MatchSettings settings) {
            if (_RoundState == RoundState.RUNNING) {
                _Logger.LogWarning($"Match is currently running, some settings may create funky behavior");
            }

            _Settings = settings;

            _Logger.LogInformation($"Match settings:" +
                $"\n\tKillGoal: {_Settings.KillGoal}"
            );

            _MatchEvents.EmitMatchSettingsEvent(_Settings);
        }

        public void SetAutoChallengeSettings(AutoChallengeSettings auto) {
            if (_RoundState == RoundState.RUNNING) {
                _Logger.LogWarning($"Not changing auto challenge settings, as match is running");
                return;
            }

            _AutoSettings = auto;
            _MatchEvents.EmitAutoSettingsChange(_AutoSettings);
        }

        public void SetRunnerName(int index, string? runnerName) {
            if (_Players.TryGetValue(index, out TrackedPlayer? player) == true) {
                player.RunnerName = runnerName ?? $"Runner {index + 1}";
            } else {
                _Logger.LogWarning($"Cannot set runner name for {index}, not in _Players");
            }
        }

        public void IncrementScore(int index) {
            if (_Players.TryGetValue(index, out TrackedPlayer? player) == true) {
                player.Streak++;
                SetScore(index, player.Score + 1);
            }
        }

        public void SetScore(int index, int score) {
            if (_Players.TryGetValue(index, out TrackedPlayer? player) == true) {
                player.Score = score;
                _MatchEvents.EmitPlayerUpdateEvent(player.Index, player);

                if (player.Score >= _Settings.KillGoal) {
                    _MatchMessages.Log($"Team {index}:{player.RunnerName} reached goal {_Settings.KillGoal}, ending match");
                    StopRound(index);
                }
            } else {
                _Logger.LogWarning($"Cannot set score of runner {index}, _Players does not contain");
            }
        }

        public int? GetScore(int index) {
            if (_Players.TryGetValue(index, out TrackedPlayer? player) == true) {
                return player.Score;
            }

            _Logger.LogWarning($"Cannot get score of runner {index}, _Players does not contain");
            return null;
        }

        public TrackedPlayer? GetPlayer(int index) {
            if (_Players.TryGetValue(index, out TrackedPlayer? player) == true) {
                return player;
            }
            return null;
        }

        private void OnTick(object? sender, SecondTimerArgs args) {
            if (_RoundState != RoundState.RUNNING) {
                return;
            }

            _MatchTicks += args.ElapsedTicks;

            int matchLength = (int)Math.Round(_MatchTicks / TICKS_PER_SECOND);

            _MatchEvents.EmitTimerEvent(matchLength);

            if (_AutoSettings.Enabled) {
                if ((matchLength - _AutoSettings.StartDelay) % _AutoSettings.Interval == 0) {
                    _Logger.LogInformation($"Starting new auto challenge");
                    StartAutoChallenge();
                }
            }

            foreach (IndexedChallenge entry in _Challenges.GetRunning()) {
                if (entry.Challenge.DurationType != ChallengeDurationType.TIMED) {
                    continue;
                }

                entry.TickCount += args.ElapsedTicks;
                //_Logger.LogTrace($"{entry.Index} {entry.Challenge.ID}/{entry.Challenge.Name} total ticks: {entry.TickCount}");

                _ChallengeEvents.EmitChallengeUpdate(entry);

                if ((int) Math.Round(entry.TickCount / TICKS_PER_SECOND) > entry.Challenge.Duration) {
                    _Logger.LogDebug($"{entry.Index} {entry.Challenge.ID}/{entry.Challenge.Name} done");
                    _Challenges.End(entry.Index);
                }
            }
        }

        public async Task StartMatch() {
            // Join the voice channel
            // Create the thread
            // Name the thread
            // 

            if (_MatchState != MatchState.UNSTARTED) {
                _Logger.LogWarning($"Cannot start match, state is not UNSTARTED, currently is {_MatchState}");
                return;
            }

            SetMatchState(MatchState.STARTED);

            if (await _ThreadManager.CreateMatchThread() == false) {
                _Logger.LogWarning($"Failed to create match thread");
                return;
            }

            await _ThreadManager.SendThreadMessage($"Match created {DateTime.UtcNow.GetDiscordFormat()}");

            if (await _ThreadManager.ConnectToVoice() == false) {
                _Logger.LogWarning($"Failed to connect to voice");
            }
            _Logger.LogInformation($"Successfully started match");
        }

        public async Task EndMatch() {
            if (_MatchState != MatchState.STARTED) {
                _Logger.LogWarning($"Cannot end match, state is not STARTED, currently is {_MatchState}");
                return;
            }

            if (await _ThreadManager.CloseThread() == false) {
                _Logger.LogWarning($"Failed to close message thread");
            }

            if (await _ThreadManager.DisconnectFromVoice() == false) {
                _Logger.LogWarning($"Failed to disconnect from voice");
            }

            SetMatchState(MatchState.UNSTARTED);
        }

        public async Task StartRound() {
            if (_RoundState == RoundState.RUNNING) {
                _Logger.LogWarning($"Not starting match, already started");
                return;
            }

            if (_RoundState == RoundState.UNSTARTED) {
                _MatchTicks = 0;
                _MatchStart = DateTime.UtcNow;
                _AdminMessages.Log($"Match unstarted, resetting ticks and start");
            }

            SetRoundState(RoundState.RUNNING);

            _AdminMessages.Log($"Match started at {_MatchStart}");
            await _ThreadManager.SendThreadMessage($"Round started at {_MatchStart.GetDiscordFormat()}");
        }

        public async Task ClearMatch() {
            foreach (TrackedPlayer player in GetPlayers()) {
                player.Score = 0;
                player.Scores = new List<ScoreEvent>();
                player.Kills = new List<KillEvent>();
                player.ValidKills = new List<KillEvent>();
                player.Deaths = new List<KillEvent>();
                player.Exp = new List<ExpEvent>();
                player.Streak = 0;
                player.Streaks = new List<int>();
                player.Characters = new List<Character>();
                player.Wins = 0;
            }

            _Players.Clear();

            _MatchTicks = 0;

            SetRoundState(RoundState.UNSTARTED);
            _MatchEvents.EmitTimerEvent(0);

            _MatchStart = DateTime.UtcNow;
            _MatchEnd = null;

            _AdminMessages.Clear();
            _MatchMessages.Clear();

            _AdminMessages.Log($"Match cleared at {DateTime.UtcNow}");
        }

        public async Task RestartRound() {
            _MatchStart = DateTime.UtcNow;
            _MatchEnd = null;
            _MatchTicks = 0;

            _MatchEvents.EmitTimerEvent(0);

            foreach (TrackedPlayer player in GetPlayers()) {
                player.Score = 0;
                player.Scores = new List<ScoreEvent>();
                player.Kills = new List<KillEvent>();
                player.ValidKills = new List<KillEvent>();
                player.Deaths = new List<KillEvent>();
                player.Exp = new List<ExpEvent>();
                player.Streak = 0;
                player.Streaks = new List<int>();
            }

            SetRoundState(RoundState.UNSTARTED);

            _AdminMessages.Log($"Match restarted at {DateTime.UtcNow}");
        }

        public async Task PauseRound() {
            SetRoundState(RoundState.PAUSED);

            _AdminMessages.Log($"Round paused at {DateTime.UtcNow:u}");

            await _ThreadManager.SendThreadMessage($"Round paused at {DateTime.UtcNow.GetDiscordFormat()}");
        }

        public async Task StopRound(int? winnerIndex = null) {
            _MatchEnd = DateTime.UtcNow;

            string s = $"Round over at {DateTime.UtcNow.GetDiscordFormat()}";

            if (winnerIndex != null) {
                if (_Players.TryGetValue(winnerIndex.Value, out TrackedPlayer? runner) == true) {
                    runner.Wins += 1;
                    _MatchEvents.EmitPlayerUpdateEvent(runner.Index, runner);

                    s += $"\n**Winner:** {runner.RunnerName}";
                } else {
                    s += $"ERROR: Cannot set winner to index {winnerIndex.Value}, _Players does not have";
                    _Logger.LogWarning($"Cannot set winner to index {winnerIndex.Value}, _Players does not have");
                }
            }

            _Logger.LogInformation($"Match finished at {_MatchEnd:u}");
            _AdminMessages.Log($"Match stopped at {_MatchEnd:u}");
            await _ThreadManager.SendThreadMessage(s);

            SetRoundState(RoundState.FINISHED);
        }

        private void StartAutoChallenge() {
            if (_AutoSettings.OptionCount <= 0) {
                _Logger.LogWarning($"Cannot start auto poll, there are 0 options");
                return;
            }

            List<IRunChallenge> challenges = _Challenges.GetActive().Shuffle();
            if (_AutoSettings.OptionCount > challenges.Count) {
                _Logger.LogWarning($"Setting auto challenge option count to {challenges.Count}, was {_AutoSettings.OptionCount}, which is more than options available");
                _AutoSettings.OptionCount = challenges.Count;
            }

            ChallengePollOptions options = new ChallengePollOptions() {
                Possible = challenges.Take(_AutoSettings.OptionCount).Select(i => i.ID).ToList(),
                VoteTime = _AutoSettings.PollTime
            };

            _Challenges.StartPoll(options);
        }

        private void SetRoundState(RoundState state) {
            if (_RoundState == state) {
                _Logger.LogDebug($"Not setting round state to {state}, is the current one");
                return;
            }

            _RoundState = state;
            _MatchEvents.EmitRoundStateEvent(_RoundState);
        }

        private void SetMatchState(MatchState state) {
            if (_MatchState == state) {
                _Logger.LogDebug($"Not setting match state to {state}, is the current one");
                return;
            }

            _MatchState = state;
            _MatchEvents.EmitMatchStateEvent(_MatchState);
        }

        private async void KillHandler(object? sender, Ps2EventArgs<KillEvent> args) {
            if (_RoundState != RoundState.RUNNING) {
                return;
            }

            KillEvent ev = args.Payload;

            string sourceFactionID = Loadout.GetFaction(ev.LoadoutID);
            string targetFactionID = Loadout.GetFaction(ev.TargetLoadoutID);

            foreach (KeyValuePair<int, TrackedPlayer> entry in _Players) {
                int index = entry.Key;
                TrackedPlayer player = entry.Value;

                bool emit = false;

                foreach (Character c in player.Characters) {
                    if (ev.SourceID == c.ID && ev.TargetID == c.ID) {
                        _Logger.LogInformation($"Player {index} committed suicide");
                        _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} SUICIDE");

                        if (player.Streak > 1) {
                            player.Streaks.Add(player.Streak);
                            player.Streak = 0;
                        }

                        player.Deaths.Add(ev);

                        emit = true;
                    } else if (c.ID == ev.SourceID) {
                        emit = await HandleNotSuicideKill(args, index, player, c);
                    } else if (c.ID == ev.TargetID) {
                        if (player.Streak > 1) {
                            player.Streaks.Add(player.Streak);
                        }
                        player.Streak = 0;

                        player.Deaths.Add(ev);

                        _Logger.LogInformation($"Player {index}:{player.RunnerName} on {c.Name} death");
                        _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} DEATH, faction {sourceFactionID}");

                        emit = true;
                    } else {
                        //_Logger.LogInformation($"Kill source:{ev.SourceID}, target:{ev.TargetID} was not {player.ID}");
                    }
                }

                if (emit == true) {
                    _MatchEvents.EmitPlayerUpdateEvent(index, player);
                }
            }
        }

        private async Task<bool> HandleNotSuicideKill(Ps2EventArgs<KillEvent> args, int index, TrackedPlayer player, Character c) {
            KillEvent ev = args.Payload;

            string sourceFactionID = Loadout.GetFaction(ev.LoadoutID);
            string targetFactionID = Loadout.GetFaction(ev.TargetLoadoutID);

            bool emit = false;

            if (sourceFactionID == targetFactionID) {
                _Logger.LogInformation($"Player {index}:{player.RunnerName} on {c.Name} TK");
                _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} got a TK");
            } else {
                //_Logger.LogInformation($"Player {index}:{player.RunnerName} kill");
                player.Kills.Add(ev);

                if (targetFactionID == "4") {
                    // Wait for the EXP events to show up
                    await Task.Delay(1000);

                    ExpEvent? expEvent = null;
                    for (int i = player.Exp.Count - 1; i >= 0; --i) {
                        ExpEvent exp = player.Exp[i];
                        //_Logger.LogTrace($"Finding exp event from {i}, got {exp.ExpID} {exp.Timestamp}, looking for timestamp {ev.Timestamp}");
                        if (exp.Timestamp < ev.Timestamp) {
                            _Logger.LogTrace($"{exp.Timestamp} is less than {ev.Timestamp}, leaving now");
                            break;
                        }

                        if (exp.Timestamp == ev.Timestamp && Experience.IsKill(exp.ExpID) && exp.TargetID == ev.TargetID) {
                            //_Logger.LogTrace($"Found {ev.Timestamp} in {exp.ExpID} {exp.Timestamp}");
                            expEvent = exp;
                            break;
                        }
                    }

                    if (expEvent == null) {
                        _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} Missing kill exp event, assuming to be a TK");
                        return false;
                    }
                }

                _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} exp event for kill");

                PsItem? weapon = await _ItemCollection.GetByID(ev.WeaponID);
                if (weapon != null) {
                    if (ItemCategory.IsValidSpeedrunnerWeapon(weapon) == true) {
                        player.Streak += 1;
                        player.ValidKills.Add(ev);

                        int score = 1;

                        List<IndexedChallenge> runningChallenges = _Challenges.GetRunning();
                        foreach (IndexedChallenge challenge in runningChallenges) {
                            bool met = await challenge.Challenge.WasMet(ev, weapon);

                            if (_Challenges.GetMode() == ChallengeMode.MEAN) {
                                if (met == false) {
                                    _Logger.LogTrace($"Team {index}:{player.RunnerName} @{c.Name} failed challenge {challenge.Challenge.ID}/{challenge.Challenge.Name}");
                                    score = 0;
                                } else {
                                    challenge.KillCount += 1;
                                    _ChallengeEvents.EmitChallengeUpdate(challenge);
                                }
                            } else if (_Challenges.GetMode() == ChallengeMode.NICE) {
                                if (met == true) {
                                    challenge.KillCount += 1;
                                    _ChallengeEvents.EmitChallengeUpdate(challenge);
                                    _Logger.LogTrace($"Team {index}:{player.RunnerName} @{c.Name} met challenge {challenge.Challenge.ID}/{challenge.Challenge.Name}, score mult {challenge.Challenge.Multiplier}");
                                    score *= challenge.Challenge.Multiplier;
                                }
                            } else {
                                _Logger.LogError($"Unknown challenge mode {_Challenges.GetMode()}");
                            }

                            if (challenge.Challenge.DurationType == ChallengeDurationType.KILLS && challenge.KillCount >= challenge.Challenge.Duration) {
                                _Logger.LogDebug($"Team {index}:{player.RunnerName} @{c.Name} finished challenge {challenge.Challenge.ID}/{challenge.Challenge.Name}");
                                _Challenges.End(challenge.Index);
                            }
                        }

                        if (score != 0) {
                            player.Scores.Add(new ScoreEvent() {
                                Timestamp = ev.Timestamp,
                                ScoreChange = score,
                                TotalScore = player.Score + score
                            });
                        }

                        player.Score += score;

                        _Logger.LogInformation($"Player {index}:{player.RunnerName} on {c.Name} valid weapon {score} points, {weapon.Name}/{weapon.CategoryID}");
                        _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} VALID kill {score} points, {weapon.Name}/{weapon.CategoryID}, faction {targetFactionID}");

                        if (player.Score >= _Settings.KillGoal) {
                            _Logger.LogInformation($"Player {index}:{player.RunnerName} reached goal {_Settings.KillGoal}, ending match");
                            _MatchMessages.Log($"Team {index}:{player.RunnerName} reached goal {_Settings.KillGoal}, ending match");
                            StopRound(player.Index);
                        }
                    } else {
                        _Logger.LogInformation($"Player {index}:{player.RunnerName} on {c.Name} invalid weapon, {weapon.Name}/{weapon.CategoryID}");
                        _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} INVALID kill, {weapon.Name}/{weapon.CategoryID}, faction {targetFactionID}");
                    }
                } else {
                    _MatchMessages.Log($"Team {index}:{player.RunnerName} @{c.Name} UNKNOWN WEAPON {ev.WeaponID}, faction {targetFactionID}");
                    _Logger.LogInformation($"Null weapon {ev.WeaponID}");
                }

                emit = true;
            }

            return emit;
        }

        private async void ExpHandler(object? sender, Ps2EventArgs<ExpEvent> args) {
            if (_RoundState != RoundState.RUNNING) {
                return;
            }

            ExpEvent ev = args.Payload;

            TrackedPlayer? runner = _GetRunnerFromID(ev.SourceID);
            if (runner == null) {
                runner = _GetRunnerFromID(ev.TargetID);
            }

            if (runner == null) {
                return;
            }

            runner.Exp.Add(ev);

            string direction = "SOURCE";
            Character? c = _GetCharacterFromID(ev.SourceID);

            if (c == null) {
                direction = "TARGET";
                c = _GetCharacterFromID(ev.TargetID);
            }

            if (c == null) {
                direction = "UNKNOWN";
            }

            ExpEntry? entry = await _ExpCollection.GetByID(ev.ExpID);
            _MatchMessages.Log($"Team {runner.Index}:{runner.RunnerName} @{c?.Name} {direction} {entry?.Description ?? $"missing {ev.ExpID}"}");
        }

        private TrackedPlayer? _GetRunnerFromID(string charID) {
            foreach (KeyValuePair<int, TrackedPlayer> entry in _Players) {
                foreach (Character c in entry.Value.Characters) {
                    if (c.ID == charID) {
                        return entry.Value;
                    }
                }
            }

            return null;
        }

        private Character? _GetCharacterFromID(string charID) {
            foreach (KeyValuePair<int, TrackedPlayer> entry in _Players) {
                foreach (Character c in entry.Value.Characters) {
                    if (c.ID == charID) {
                        return c;
                    }
                }
            }

            return null;
        }

        public RoundState GetRoundState() => _RoundState;
        public MatchState GetMatchState() => _MatchState;
        public DateTime GetMatchStart() => _MatchStart;
        public DateTime? GetMatchEnd() => _MatchEnd;
        public List<TrackedPlayer> GetPlayers() => _Players.Values.ToList();
        public int GetMatchLength() => (int)Math.Round(_MatchTicks / TICKS_PER_SECOND);
        public MatchSettings GetSettings() => _Settings;
        public AutoChallengeSettings GetAutoChallengeSettings() => _AutoSettings;


    }
}
