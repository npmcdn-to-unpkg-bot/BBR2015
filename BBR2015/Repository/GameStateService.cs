﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web.Script.Serialization;
using Database;
using Database.Entities;
using Newtonsoft.Json;
using System.Data.Entity;

namespace Repository
{
    // Registrert som Singleton
    public class GameStateService
    {
        private readonly DataContextFactory _dataContextFactory;
        private readonly CurrentMatchProvider _currentMatchProvider;

        private ConcurrentDictionary<Guid, MatchState> _matchStates = new ConcurrentDictionary<Guid, MatchState>();

        public GameStateService(DataContextFactory dataContextFactory, CurrentMatchProvider currentMatchProvider)
        {
            _dataContextFactory = dataContextFactory;
            _currentMatchProvider = currentMatchProvider;
        }

        public void Calculate()
        {
            var matchId = _currentMatchProvider.GetMatchId();

            //using (With.ReadUncommitted()) 
            using (var context = _dataContextFactory.Create())
            {
                var sorterteLag =
                    context.LagIMatch.Include(x => x.Lag.Deltakere).Include(x => x.VåpenBeholdning).Include("Våpenbeholdning.BruktIPostRegistrering")
                           .Where(x => x.Match.MatchId == matchId)
                           .OrderByDescending(x => x.PoengSum)
                           .ToList();                

                // Legg på poeng fra Achievements 
                var achievementsPoeng = from a in context.Achievements
                                        group a by a.LagId
                                            into g
                                        select new { LagId = g.Key, Poeng = g.Sum(x => x.Score), Achievements = g };

                foreach (var lagPoeng in achievementsPoeng)
                {
                    var p = lagPoeng;
                    var lag = sorterteLag.Single(x => x.Lag.LagId == p.LagId);
                    lag.PoengSum += p.Poeng;
                }

                var poster = (from p in context.PosterIMatch.Include(x => x.Post)
                              where p.Match.MatchId == matchId
                              select new TempPost
                              {
                                  PostId = p.Post.PostId,
                                  Latitude = p.Post.Latitude,
                                  Longitude = p.Post.Longitude,
                                  CurrentPoengIndex = p.CurrentPoengIndex,
                                  PoengArray = p.PoengArray,
                                  Navn = p.Post.Navn,
                                  ErSynlig = p.SynligFraTid < TimeService.Now && TimeService.Now < p.SynligTilTid,
                                  SynligFra = p.SynligFraTid,
                                  SynligTil = p.SynligTilTid,
                                  RiggetMedVåpen = p.RiggetVåpen
                              }).ToList();

                var postRegistreringer = (from l in context.LagIMatch
                                          from p in l.PostRegistreringer
                                          where l.Match.MatchId == matchId
                                          select new
                                          {
                                              PostId = p.RegistertPost.Post.PostId,
                                              LagIMatchId = p.RegistertForLag.Id,
                                              Poeng = p.PoengForRegistrering,
                                              Deltaker = p.RegistrertAvDeltaker
                                          }).ToList();


                var rankedeLag = (from l in sorterteLag
                                  select new
                                  {
                                      Id = l.Id,
                                      Lag = l.Lag,
                                      Rank = sorterteLag.Count(x => x.PoengSum > l.PoengSum) + 1,
                                      PoengSum = l.PoengSum,
                                      LagIMatch = l
                                  }).OrderBy(x => x.Rank).ToList();

                var poengsummer = (from l in rankedeLag
                                   select l.PoengSum).Distinct().OrderByDescending(x => x).ToList();

                var nyGameState = new Dictionary<string, GameStateForLag>();
                var random = new Random();

                foreach (var lag in rankedeLag)
                {
                    var egenPoengIndex = poengsummer.IndexOf(lag.PoengSum);

                    var poengForover = egenPoengIndex == 0 ? lag.PoengSum : poengsummer[egenPoengIndex - 1];
                    var poengBakover = egenPoengIndex == poengsummer.Count - 1 ? lag.PoengSum : poengsummer[egenPoengIndex + 1];

                    var state = new GameStateForLag
                    {
                        LagId = lag.Lag.LagId,
                        LagNavn = lag.Lag.Navn,
                        LagFarge = lag.Lag.Farge,
                        LagIkon = lag.Lag.Ikon,
                        Score = lag.PoengSum,
                        Ranking = new GameStateRanking
                        {
                            Rank = lag.Rank,
                            PoengBakLagetForan = poengForover - lag.PoengSum,
                            PoengForanLagetBak = lag.PoengSum - poengBakover,
                        },
                        Poster = (from p in poster.Where(x => x.ErSynlig)
                                  join r in postRegistreringer.Where(x => x.LagIMatchId == lag.Id) on p.PostId equals r.PostId into j
                                  from reg in j.DefaultIfEmpty()
                                  select new GameStatePost
                                  {
                                      PostId = p.PostId.ToString(),
                                      Navn = p.Navn,
                                      Latitude = p.Latitude,
                                      Longitude = p.Longitude,
                                      PoengVerdi = reg != null ? reg.Poeng : PostIMatch.BeregnPoengForNesteRegistrering(p.PoengArray, p.CurrentPoengIndex),
                                      HarRegistrert = reg != null,
                                  }).OrderBy(x => x.Navn).ToList(),
                        Vaapen = lag.LagIMatch.VåpenBeholdning.Where(x => x.BruktIPostRegistrering == null).Select(x => new GameStateVaapen { VaapenId = x.VaapenId }).ToList(),
                        Deltakere = lag.Lag.Deltakere.Select(x => new GameStateDeltaker { DeltakerId = x.DeltakerId, Navn = x.Navn }).ToList(),
                        Achievements = new List<GameStateAchievement>()//achievementsPoeng.Where(x => x.LagId == lag.Lag.LagId).Select(x => x.Achievements)
                    };

                    nyGameState.Add(state.LagId, state);
                }

                var scoreboard = new ScoreboardState();
                scoreboard.Poster = (from p in poster
                                     select new ScoreboardPost
                                     {
                                         PostId = p.PostId.ToString(),
                                         Latitude = p.Latitude,
                                         Longitude = p.Longitude,
                                         ErSynlig = p.ErSynlig,
                                         Navn = p.Navn,
                                         Verdi = PostIMatch.BeregnPoengForNesteRegistrering(p.PoengArray, p.CurrentPoengIndex),
                                         AntallRegistreringer = postRegistreringer.Count(x => x.PostId == p.PostId),
                                         SynligFra = p.SynligFra,
                                         RiggetMedVåpen = p.RiggetMedVåpen
                                     }).OrderBy(x => x.Navn).ToList();

                scoreboard.Lag = sorterteLag.Select(l => new ScoreboardLag
                {
                    LagId = l.Lag.LagId,
                    LagNavn = l.Lag.Navn,
                    LagFarge = l.Lag.Farge,
                    Score = l.PoengSum,
                    Ranking = sorterteLag.Count(x => x.PoengSum > l.PoengSum) + 1,
                    AntallRegistreringer = postRegistreringer.Count(x => x.LagIMatchId == l.Id)
                }).ToList();

                var deltakerPoeng = postRegistreringer
                                        .GroupBy(p => p.Deltaker.DeltakerId)
                                        .Select(g => new
                                        {
                                            DeltakerId = g.Key,
                                            Navn = g.First().Deltaker.Navn,
                                            LagIMatchId = g.First().LagIMatchId,
                                            AntallRegistreringer = g.Count(),
                                            Poengsum = g.Sum(x => x.Poeng)
                                        }).ToList();

                scoreboard.Deltakere = (from p in deltakerPoeng
                                        join l in sorterteLag on p.LagIMatchId equals l.Id
                                        select new ScoreboardDeltaker
                                        {
                                            DeltakerId = p.DeltakerId,
                                            Navn = p.Navn,
                                            AntallRegistreringer = p.AntallRegistreringer,
                                            Score = p.Poengsum,
                                            LagId = l.Lag.LagId,
                                            LagFarge = l.Lag.Farge,
                                            LagIkon = l.Lag.Ikon,
                                            LagNavn = l.Lag.Navn,
                                            MostValueablePlayerRanking = deltakerPoeng.Count(x => x.Poengsum > p.Poengsum) + 1
                                        }).OrderByDescending(x => x.MostValueablePlayerRanking).ToList();

                var førsteTidspunktEtterNå = (from p in poster
                                              from t in p.Tider
                                              where t > TimeService.Now
                                              select t).Union(new[] { DateTime.MaxValue }).Min();

                scoreboard.Match = LagScoreboardMatchInfo(context, matchId);

                // swap current state
                _matchStates[matchId] = new MatchState(matchId, nyGameState, scoreboard, førsteTidspunktEtterNå);

                // IKKE SAVE CHANGES
            }
        }

        private ScoreboardMatch LagScoreboardMatchInfo(DataContext context, Guid matchId)
        {
            var match = context.Matcher.SingleOrDefault(x => x.MatchId == matchId);

            if (match == null)
                return new ScoreboardMatch();

            var MaxLatitude = match.GeoboxNWLatitude.GetValueOrDefault();
            var MaxLongitude = match.GeoboxSELongitude.GetValueOrDefault();
            var MinLatitude = match.GeoboxSELatitude.GetValueOrDefault();
            var MinLongitude = match.GeoboxNWLongitude.GetValueOrDefault();

            var CenterLatitude = Math.Round((MinLatitude + MaxLatitude) / 2, 5);
            var CenterLongitude = Math.Round((MinLongitude + MaxLongitude) / 2, 5);

            var scoreboardMatch = new ScoreboardMatch
            {
                Navn = match.Navn,
                CenterMap = new List<double> { CenterLatitude, CenterLongitude }
            };

            return scoreboardMatch;
        }

        public GameStateForLag Get(string lagId)
        {
            var matchId = _currentMatchProvider.GetMatchId();

            if (!_matchStates.ContainsKey(matchId))
                Calculate();

            if (_matchStates[matchId].ErUtløpt)
                Calculate();

            return _matchStates[matchId].Get(lagId);
        }

        public ScoreboardState GetScoreboard()
        {
            var matchId = _currentMatchProvider.GetMatchId();

            if (!_matchStates.ContainsKey(matchId))
                Calculate();

            if (_matchStates[matchId].ErUtløpt)
                Calculate();

            return _matchStates[matchId].GetScoreboard();
        }
    }

    public class MatchState
    {
        private readonly Dictionary<string, GameStateForLag> _gamestates;
        private readonly ScoreboardState _scoreboard;
        private readonly DateTime _gyldigInntil;

        public Guid MatchId { get; set; }
        public MatchState(Guid matchId, Dictionary<string, GameStateForLag> gamestates, ScoreboardState scoreboard, DateTime? gyldigInntil)
        {
            MatchId = matchId;
            _gamestates = gamestates;
            _scoreboard = scoreboard;
            _gyldigInntil = gyldigInntil ?? DateTime.MaxValue;
        }

        public bool ErUtløpt
        {
            get { return TimeService.Now > _gyldigInntil; }
        }
        public GameStateForLag Get(string lagId)
        {
            return _gamestates[lagId];
        }

        public ScoreboardState GetScoreboard()
        {
            return _scoreboard;
        }
    }

    public class ScoreboardState
    {
        public ScoreboardState()
        {
            Poster = new List<ScoreboardPost>();
            Deltakere = new List<ScoreboardDeltaker>();
            Lag = new List<ScoreboardLag>();
            Match = new ScoreboardMatch();
        }
        public List<ScoreboardPost> Poster { get; set; }
        public List<ScoreboardDeltaker> Deltakere { get; set; }
        public List<ScoreboardLag> Lag { get; set; }
        public ScoreboardMatch Match { get; set; }

    }

    public class ScoreboardMatch
    {
        public string Navn { get; set; }
        public List<double> CenterMap { get; set; } = new List<double>();
    }

    public class ScoreboardGeobox
    {
        public double MaxLatitude { get; set; }
        public double MaxLongitude { get; set; }

        public double MinLatitude { get; set; }
        public double MinLongitude { get; set; }

        public double CenterLatitude { get; set; }
        public double CenterLongitude { get; set; }

        public void CalculateCenter()
        {
            CenterLatitude = Math.Round((MinLatitude + MaxLatitude) / 2, 5);
            CenterLongitude = Math.Round((MinLongitude + MaxLongitude) / 2, 5);
        }
    }

    public class ScoreboardPost
    {
        public string Navn { get; set; }
        public double Latitude { get; set; }

        public double Longitude { get; set; }

        public int AntallRegistreringer { get; set; }
        public int Verdi { get; set; }
        public bool ErSynlig { get; set; }
        public DateTime SynligFra { get; set; }
        public string RiggetMedVåpen { get; set; }
        public string PostId { get; set; }
    }

    public class ScoreboardDeltaker
    {
        public string Navn { get; set; }
        public string DeltakerId { get; set; }
        public string LagId { get; set; }
        public string LagNavn { get; set; }
        public string LagFarge { get; set; }
        public string LagIkon { get; set; }
        public int AntallRegistreringer { get; set; }
        public int Score { get; set; }
        public int MostValueablePlayerRanking { get; set; }
    }

    public class ScoreboardLag
    {
        public string LagId { get; set; }
        public string LagNavn { get; set; }
        public string LagFarge { get; set; }
        public int AntallRegistreringer { get; set; }
        public int Score { get; set; }
        public int Ranking { get; set; }
    }
    public class TempPost
    {
        public Guid PostId { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int CurrentPoengIndex { get; set; }
        public string PoengArray { get; set; }

        [ScriptIgnore]
        public bool ErSynlig { get; set; }

        public string Navn { get; set; }
        public DateTime[] Tider { get { return new[] { SynligFra, SynligTil }; } }
        public DateTime SynligTil { get; set; }
        public DateTime SynligFra { get; set; }
        public string RiggetMedVåpen { get; set; }
    }

    public class GameStateForLag
    {
        public string LagNavn { get; set; }
        public GameStateForLag()
        {
            Poster = new List<GameStatePost>();
        }
        public List<GameStatePost> Poster { get; set; }

        public int Score { get; set; }
        public List<GameStateVaapen> Vaapen { get; set; }
        public List<GameStateAchievement> Achievements { get; set; }
        public GameStateRanking Ranking { get; set; }
        public string LagFarge { get; set; }
        public string LagIkon { get; set; }
        public string LagId { get; set; }
        public List<GameStateDeltaker> Deltakere { get; set; }
    }

    public class GameStateAchievement
    {
        public string Achievement { get; set; }
    }

    public class GameStateRanking
    {
        public int Rank { get; set; }
        public int PoengForanLagetBak { get; set; }
        public int PoengBakLagetForan { get; set; }
    }
    public class GameStateVaapen
    {
        public string VaapenId { get; set; }
        public string Beskrivelse { get; set; }
    }

    public class GameStatePost
    {
        public string PostId { get; set; }
        public string Navn { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public bool HarRegistrert { get; set; }
        public int PoengVerdi { get; set; }        
    }

    public class GameStateDeltaker
    {
        public string DeltakerId { get; set; }
        public string Navn { get; set; }
    }
}