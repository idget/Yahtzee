﻿using System;
using System.Collections.Concurrent;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNet.Identity;
using Microsoft.AspNet.SignalR;
using Website.DAL;
using Website.DAL.Entities;
using Website.HubHelpers;
using Website.Models;
using Yahtzee.Framework;

namespace Website
{
	public class YahtzeeHub : Hub
	{
		private static readonly ConcurrentDictionary<string, GameStateModel> StateDict = new ConcurrentDictionary<string, GameStateModel>();

		private readonly Func<IDiceCup> _diceCupFactory;
		private readonly ILifetimeScope _hubScope;
		private readonly Func<IScoreSheet> _scoreSheetFactory;
		private readonly ApplicationDbContext _dbContext;
		private readonly DbSet<ApplicationUser> _userRepository;

		// This isn't awesome but to control the lifetime scope of the hub's dependencies,
		// the root container needs to be passed in, similar to a service locator.
		// See http://autofac.readthedocs.org/en/latest/integration/signalr.html
		public YahtzeeHub(ILifetimeScope rootScope)
		{
			_hubScope = rootScope.BeginLifetimeScope();

			_scoreSheetFactory = _hubScope.Resolve<Func<IScoreSheet>>();
			_diceCupFactory = _hubScope.Resolve<Func<IDiceCup>>();
			_dbContext = _hubScope.Resolve<ApplicationDbContext>();
			_userRepository = _dbContext.Set<ApplicationUser>();
		}

		public override Task OnConnected()
		{
			GetOrCreateState();
			return base.OnConnected();
		}

		public override Task OnDisconnected(bool stopCalled)
		{
			GameStateModel removedModel;
			StateDict.TryRemove(Context.ConnectionId, out removedModel);
			return base.OnDisconnected(stopCalled);
		}

		public void RollDice()
		{
			var state = GetOrCreateState();

			if(state.CurrentDiceCup.IsFinal())
			{
				return;
			}

			var rollResult = state.CurrentDiceCup.Roll();
			if(rollResult != null)
			{
				var rollData = new
				{
					dice = rollResult.Select(x => x.Value).ToList(),
					rollCount = state.CurrentDiceCup.RollCount,
					isFinal = state.CurrentDiceCup.IsFinal()
				};

				Clients.Caller.processRoll(rollData);
			}
			else
			{
				state.CurrentDiceCup = _diceCupFactory();
			}
		}

		public void TakeUpper(int number)
		{
			var state = GetOrCreateState();

			if(state.CurrentDiceCup.RollCount == 0)
			{
				return;
			}

			var section = (UpperSectionItem) number;
			state.ScoreSheet.RecordUpperSection(section, state.CurrentDiceCup);

			var score = GetScoreForUpperSection(section, state.ScoreSheet);

			state.CurrentDiceCup = _diceCupFactory();

			var isUpperSectionComplete = state.ScoreSheet.IsUpperSectionComplete;
			int? upperSectionScore = null;
			int? upperSectionBonus = null;
			int? upperSectionTotal = null;
			if(isUpperSectionComplete)
			{
				upperSectionScore = state.ScoreSheet.UpperSectionTotal;
				upperSectionBonus = state.ScoreSheet.UpperSectionBonus;
				upperSectionTotal = state.ScoreSheet.UpperSectionTotalWithBonus;
			}

			var isScoreSheetComplete = state.ScoreSheet.IsScoreSheetComplete;
			int? grandTotal = null;
			if(state.ScoreSheet.IsScoreSheetComplete)
			{
				grandTotal = state.ScoreSheet.GrandTotal;
				SaveStatisticsAsync(isGameComplete: true);
			}

			Clients.Caller.setUpper(new
			{
				upperNum = number,
				score,
				isUpperSectionComplete,
				upperSectionScore,
				upperSectionBonus,
				upperSectionTotal,
				isScoreSheetComplete,
				grandTotal
			});
		}

		public void TakeLower(string name)
		{
			var state = GetOrCreateState();

			if(state.CurrentDiceCup.RollCount == 0)
			{
				return;
			}

			var score = LowerSectionScorer.Score[name](state.ScoreSheet, state.CurrentDiceCup);

			state.CurrentDiceCup = _diceCupFactory();
			var isLowerSectionComplete = state.ScoreSheet.IsLowerSectionComplete;
			int? lowerSectionTotal = null;
			if(isLowerSectionComplete)
			{
				lowerSectionTotal = state.ScoreSheet.LowerSectionTotal;
			}
			var isScoreSheetComplete = state.ScoreSheet.IsScoreSheetComplete;
			int? grandTotal = null;
			if(state.ScoreSheet.IsScoreSheetComplete)
			{
				grandTotal = state.ScoreSheet.GrandTotal;
				SaveStatisticsAsync(isGameComplete: true);
			}

			Clients.Caller.setLower(new
			{
				name,
				score = score ?? -1,
				isLowerSectionComplete,
				lowerSectionTotal,
				isScoreSheetComplete,
				grandTotal
			});
		}

		public void ToggleHoldDie(int index)
		{
			var state = GetOrCreateState();

			if(state.CurrentDiceCup.IsFinal() || state.CurrentDiceCup.RollCount == 0)
			{
				return;
			}

			if(state.CurrentDiceCup.Dice[index].State == DieState.Held)
			{
				state.CurrentDiceCup.Unhold(index);
			}
			else
			{
				state.CurrentDiceCup.Hold(index);
			}

			Clients.Caller.toggleHoldDie(new
			{
				index,
				dieState = state.CurrentDiceCup.Dice[index].State.ToString()
			});
		}

		private void SaveStatisticsAsync(bool isGameComplete)
		{
			var state = GetOrCreateState();
			if(state.UserId != null)
			{
				var user = _userRepository.Find(state.UserId);
				if(user != null)
				{
					var statistic = new GameStatistic
					{
						User = user,
						FinalScore = state.ScoreSheet.GrandTotal,
						GameCompleted = isGameComplete,
						GameEndTime = DateTime.UtcNow,
						GameStartTime = state.GameStartTime
					};
					user.GameStatistics.Add(statistic);
					_dbContext.SaveChanges();
				}
			}
		}

		private static int GetScoreForUpperSection(UpperSectionItem section, IScoreSheet scoreSheet)
		{
			switch(section)
			{
				case UpperSectionItem.Ones:
					return scoreSheet.Ones ?? -1;
				case UpperSectionItem.Twos:
					return scoreSheet.Twos ?? -1;
				case UpperSectionItem.Threes:
					return scoreSheet.Threes ?? -1;
				case UpperSectionItem.Fours:
					return scoreSheet.Fours ?? -1;
				case UpperSectionItem.Fives:
					return scoreSheet.Fives ?? -1;
				case UpperSectionItem.Sixes:
					return scoreSheet.Sixes ?? -1;
			}

			return 0;
		}

		private GameStateModel GetOrCreateState()
		{
			var currentConnectionId = Context.ConnectionId;
			var currentUser = Context.User;

			string userId = null;
			if(currentUser != null)
			{
				userId = currentUser.Identity.GetUserId<string>();
			}

			GameStateModel state;
			var stateExists = StateDict.TryGetValue(currentConnectionId, out state);

			if(!stateExists)
			{
				state = new GameStateModel
				{
					ConnectionId = currentConnectionId,
					UserId = userId,
					ScoreSheet = _scoreSheetFactory(),
					CurrentDiceCup = _diceCupFactory()
				};
				StateDict.AddOrUpdate(currentConnectionId, state, (key, existingVal) => state);
			}

			return state;
		}

		protected override void Dispose(bool disposing)
		{
			if(disposing && _hubScope != null)
			{
				_hubScope.Dispose();
			}

			base.Dispose(disposing);
		}
	}
}