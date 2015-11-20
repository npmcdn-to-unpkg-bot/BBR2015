﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Database;
using SharpKml.Base;
using SharpKml.Dom;
using SharpKml.Dom.GX;
using TimeSpan = SharpKml.Dom.TimeSpan;

namespace Repository
{
    public class KmlService
    {
        private readonly DataContextFactory _dataContextFactory;
        private readonly CurrentMatchProvider _currentMatchProvider;

        public KmlService(DataContextFactory dataContextFactory, CurrentMatchProvider currentMatchProvider)
        {
            _dataContextFactory = dataContextFactory;
            _currentMatchProvider = currentMatchProvider;
        }

        private string LagStyleKey(string lagId)
        {
            return string.Format("style_{0}", lagId);
        }
        public string GetKml()
        {
            var matchId = _currentMatchProvider.GetMatchId();

            Kml kml = new Kml();

            var document = new Document();
            kml.Feature = document;

            var c = System.Drawing.ColorTranslator.FromHtml("#33FF33");
            var postStyle = new Style();
            postStyle.Id = "post_vanlig";
            postStyle.Icon = new IconStyle
            {
                Color = new Color32(c.A, c.R, c.G, c.B),
                Icon = new IconStyle.IconLink(new Uri("http://maps.google.com/mapfiles/kml/paddle/wht-blank.png")),
                Scale = 0.5
            };

            document.AddStyle(postStyle);

            var lagFolder = new Folder {Name = "Lag"};
            var postFolder = new Folder {Name = "Poster"};

            document.AddFeature(lagFolder);
            document.AddFeature(postFolder);


            using (var context = _dataContextFactory.Create())
            {
                var match = context.Matcher.Single(x => x.MatchId == matchId);

                var maxLat = 59.680825;
                var minLat = 59.672062;
                var minLon = 10.603076;
                var maxLon = 10.610055;

                var poster = (from pim in context.PosterIMatch
                              where pim.Match.MatchId == matchId
                              select
                                  new
                                  {
                                      pim.SynligFraTid,
                                      pim.SynligTilTid,
                                      pim.Post.Navn,
                                      pim.Post.Latitude,
                                      pim.Post.Longitude
                                  }).ToList();

                foreach (var p in poster)
                {
                    var placemark = new Placemark();
                    placemark.StyleUrl = new Uri("#post_vanlig", UriKind.Relative);
                    placemark.Time = new TimeSpan { Begin = p.SynligFraTid, End = p.SynligTilTid };
                    placemark.Name = p.Navn;
                    placemark.Geometry = new Point { Coordinate = new Vector(p.Latitude, p.Longitude) };

                    postFolder.AddFeature(placemark);
                }

                var lag = context.Lag.ToList();

                var lagDictionary = new Dictionary<string, Folder>();

                foreach (var lag1 in lag)
                {
                    var color = System.Drawing.ColorTranslator.FromHtml("#" + lag1.Farge);
                    var style = new Style();
                    style.Id = LagStyleKey(lag1.LagId);
                    style.Icon = new IconStyle
                    {
                        Color = new Color32(color.A, color.R, color.G, color.B),
                        Icon = new IconStyle.IconLink(new Uri("http://maps.google.com/mapfiles/kml/paddle/wht-blank-lv.png")),
                        Scale = 0.3
                    };

                    document.AddStyle(style);
                    var folder = new Folder {Name = lag1.Navn};
                    lagDictionary.Add(lag1.LagId, folder);
                    lagFolder.AddFeature(folder);
                }

                var deltakerPosisjoner = (from p in context.DeltakerPosisjoner
                                 join d in context.Deltakere on p.DeltakerId equals d.DeltakerId
                                 where match.StartTid < p.Tidspunkt && p.Tidspunkt < match.SluttTid
                                 select new
                                 {
                                     p.Latitude,
                                     p.Longitude,
                                     p.DeltakerId,
                                     d.Navn,
                                     d.Lag.Farge,
                                     d.Lag.LagId,
                                     p.Tidspunkt
                                 }).GroupBy(x => x.DeltakerId).ToDictionary(x => x.Key, y => y);

                foreach (var deltaker in deltakerPosisjoner)
                {
                    //if (deltaker.Key != "MS_3-2")

                    //    continue;
                    var førstePosisjon = deltaker.Value.FirstOrDefault();
                    var placemark = new Placemark();
                    placemark.StyleUrl = new Uri("#" + LagStyleKey(førstePosisjon.LagId), UriKind.Relative);
                    placemark.Name = førstePosisjon.Navn;

                    var posisjoner = deltaker.Value.OrderBy(x => x.Tidspunkt).ToList();
                           
                    var track = new Track();

                    var forrigePosisjon = førstePosisjon;
                    foreach (var pos in posisjoner)
                    {
                        // Utafor boksen
                        if (pos.Latitude > maxLat || pos.Latitude < minLat || pos.Longitude > maxLon || pos.Longitude < minLon)
                        {
                            continue;
                        }

                        var meter = DistanseKalkulator.MeterMellom(forrigePosisjon.Latitude, forrigePosisjon.Longitude, pos.Latitude, pos.Longitude);
                        var sekunder = pos.Tidspunkt.Subtract(forrigePosisjon.Tidspunkt).TotalSeconds;

                        if (sekunder > 0)
                        {
                            var fart = meter/sekunder;
                            if (fart > 8.333) // raskere enn 12 blank på 100m
                            {
                                continue;
                            }
                        }

                        track.AddWhen(pos.Tidspunkt);
                        track.AddCoordinate(new Vector(pos.Latitude, pos.Longitude));
                        forrigePosisjon = pos;
                    }

                    placemark.Geometry = track;

                    lagDictionary[førstePosisjon.LagId].AddFeature(placemark);
                }

            }


            Serializer serializer = new Serializer();
            serializer.Serialize(kml);

            return serializer.Xml;
        }
    }
}