﻿using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;


namespace KERBALISM
{

	public static class Science
	{
		// this is for auto-transmit throttling
		public const double min_file_size = 0.002;

		private class XmitFile
		{
			public string subject_id;
			public ExperimentInfo expInfo;
			public File file;
			public Drive drive;
			public bool isInWarpCache;
			public File realDriveFile; // reference to the "real" file for files in the warp cache

			public XmitFile(string subject_id, ExperimentInfo expInfo, File file, Drive drive, bool isInWarpCache, File realDriveFile = null)
			{
				this.subject_id = subject_id;
				this.expInfo = expInfo;
				this.file = file;
				this.drive = drive;
				this.isInWarpCache = isInWarpCache;
				this.realDriveFile = realDriveFile;
			}
		}

		// pseudo-ctor
		public static void Init()
		{
			// make the science dialog invisible, just once
			if (Features.Science)
			{
				GameObject prefab = AssetBase.GetPrefab("ScienceResultsDialog");
				if (Settings.ScienceDialog)
				{
					prefab.gameObject.AddOrGetComponent<Hijacker>();
				}
				else
				{
					prefab.gameObject.AddOrGetComponent<MiniHijacker>();
				}
			}
		}

		private static Drive FindDrive(Vessel v, string filename)
		{
			foreach (var d in Drive.GetDrives(v, true))
			{
				if (d.files.ContainsKey(filename))
				{
					return d;
				}
			}
			return null;
		}

		// consume EC for transmission, and transmit science data
		public static void Update(Vessel v, VesselData vd, ResourceInfo ec, double elapsed_s)
		{
			// do nothing if science system is disabled
			if (!Features.Science) return;

			// avoid corner-case when RnD isn't live during scene changes
			// - this avoid losing science if the buffer reach threshold during a scene change
			if (HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX && ResearchAndDevelopment.Instance == null)
				return;
			
			// clear list of files transmitted
			vd.filesTransmitted.Clear();

			// check connection
			if (vd.Connection == null || !vd.Connection.linked || vd.Connection.rate <= 0.0 || ec.Amount < vd.Connection.ec * elapsed_s)
				return;
			
			double transmitCapacity = vd.Connection.rate * elapsed_s;
			float scienceCredited = 0f;

			GetFilesToTransmit(v);

			if (xmitFiles.Count == 0)
				return;

			// traverse the list in reverse because :
			// - warp cache files are at the end, and they are always transmitted regerdless of transmit capacity
			// - others files are next, sorted in science value per MB ascending order 
			for (int i = xmitFiles.Count - 1; i >= 0; i--)
			{
				XmitFile xmitFile = xmitFiles[i];

				if (xmitFile.file.size == 0.0)
					continue;

				// always transmit everything in the warp cache
				if (!xmitFile.isInWarpCache && transmitCapacity <= 0.0)
					break;

				// determine how much data is transmitted
				double transmitted = xmitFile.isInWarpCache ? xmitFile.file.size : Math.Min(xmitFile.file.size, transmitCapacity);

				// consume transmit capacity
				transmitCapacity -= transmitted;

				// get science value
				float xmitScienceValue = (float)(transmitted * xmitFile.expInfo.SubjectSciencePerMB);

				// increase science points to credit
				scienceCredited += xmitScienceValue;

				// fire subject completed events
				if (!xmitFile.expInfo.SubjectIsCompleted && xmitFile.expInfo.ScienceRemainingToRetrieve < 0.1f) // large threshold because of floating point errors.
					SubjectXmitCompleted(xmitFile, v);

				// consume data in the file
				xmitFile.file.size -= transmitted;
				xmitFile.expInfo.RemoveDataCollectedInFlight(xmitScienceValue);

				if (xmitFile.isInWarpCache && xmitFile.realDriveFile != null)
				{
					xmitFile.realDriveFile.transmitRate = transmitted / elapsed_s;
					vd.filesTransmitted.Add(xmitFile.realDriveFile);
				}
				else
				{
					xmitFile.file.transmitRate = transmitted / elapsed_s;
					vd.filesTransmitted.Add(xmitFile.file);
				}

				// credit the subject
				ScienceSubject stockSubject = xmitFile.expInfo.StockSubject;
				stockSubject.science = Math.Min(stockSubject.science + (xmitScienceValue / HighLogic.CurrentGame.Parameters.Career.ScienceGainMultiplier), stockSubject.scienceCap);
				stockSubject.scientificValue = ResearchAndDevelopment.GetSubjectValue(stockSubject.science, stockSubject);
			}

			// Add science points
			// We don't use "TransactionReasons.ScienceTransmission" because AddScience fire a bunch of events,
			// triggering a lot of side issues (ex : chatterer transmit sound playing continously, strategia "+0.0 science" popup...)
			ResearchAndDevelopment.Instance.AddScience(scienceCredited, TransactionReasons.None);
			vd.scienceTransmitted += scienceCredited;
		}

		private static void GetFilesToTransmit(Vessel v)
		{
			Drive warpCache = Cache.WarpCache(v);

			xmitFiles.Clear();

			List<string> filesToRemove = new List<string>();

			foreach (var drive in Drive.GetDrives(v, true))
			{
				filesToRemove.Clear();
				foreach (var p in drive.files)
				{
					if (p.Value.size <= 0.0 && (!warpCache.files.ContainsKey(p.Key) || warpCache.files[p.Key].size == 0.0))
					{
						filesToRemove.Add(p.Key);
						continue;
					}
	
					if (drive.GetFileSend(p.Key))
					{
						p.Value.transmitRate = 0.0;
						xmitFiles.Add(new XmitFile(p.Key, Experiment(p.Key), p.Value, drive, false));
					}
				}

				foreach (string fileToRemove in filesToRemove)
					drive.files.Remove(fileToRemove);
			}

			// sort files by science value per MB ascending order so high value files are transmitted first
			// because XmitFile list is processed from end to start
			xmitFiles.Sort((x, y) => x.expInfo.SubjectSciencePerMB.CompareTo(y.expInfo.SubjectSciencePerMB));

			// add all warpcache files to the end of the XmitFile list
			foreach (var p in warpCache.files)
			{
				// don't transmit empty files
				if (p.Value.size <= 0.0)
					continue;

				XmitFile driveFile = xmitFiles.Find(pr => pr.subject_id == p.Key);
				if (driveFile != null)
					xmitFiles.Add(new XmitFile(p.Key, Experiment(p.Key), p.Value, warpCache, true, driveFile.file));
				else
					xmitFiles.Add(new XmitFile(p.Key, Experiment(p.Key), p.Value, warpCache, true)); // should not be happening, but better safe than sorry
			}
		}

		private static void SubjectXmitCompleted(XmitFile xmitFile, Vessel v)
		{
			// remember that the subject is complete
			xmitFile.expInfo.SubjectIsCompleted = true;

			// fire science transmission game event. This is used by stock contracts and a few other things.
			GameEvents.OnScienceRecieved.Fire(xmitFile.expInfo.ScienceValue, xmitFile.expInfo.StockSubject, v.protoVessel, false);

			// fire our API event
			// Note (GOT) : disabled, nobody is using it and i'm not sure what is the added value compared to the stock event,
			// unless we fire it for every transmission, and in this case this is a very bad idea from a performance standpoint
			// API.OnScienceReceived.Notify(credits, subject, pv, true);

			// notify the player
			string subjectResultText;
			if (string.IsNullOrEmpty(xmitFile.file.resultText))
			{
				subjectResultText = Lib.TextVariant(
					"Our researchers will jump on it right now",
					"This cause some excitement",
					"These results are causing a brouhaha in R&D",
					"Our scientists look very confused",
					"The scientists won't believe these readings");
			}
			else
			{
				subjectResultText = xmitFile.file.resultText;
			}
			subjectResultText = Lib.WordWrapAtLength(subjectResultText, 70);
			Message.Post(Lib.BuildString(xmitFile.expInfo.SubjectName, " completed\n", Lib.HumanReadableScience(xmitFile.expInfo.ScienceValue)), subjectResultText);
		}

		// return module acting as container of an experiment
		public static IScienceDataContainer Container(Part p, string experiment_id)
		{
			// first try to get a stock experiment module with the right experiment id
			// - this support parts with multiple experiment modules, like eva kerbal
			foreach (ModuleScienceExperiment exp in p.FindModulesImplementing<ModuleScienceExperiment>())
			{
				if (exp.experimentID == experiment_id) return exp;
			}

			// if none was found, default to the first module implementing the science data container interface
			// - this support third-party modules that implement IScienceDataContainer, but don't derive from ModuleScienceExperiment
			return p.FindModuleImplementing<IScienceDataContainer>();
		}


		// return info about an experiment
		public static ExperimentInfo Experiment(string subject_id)
		{
			ExperimentInfo info;
			if (!experiments.TryGetValue(subject_id, out info))
			{
				info = new ExperimentInfo(subject_id);
				experiments.Add(subject_id, info);
			}
			return info;
		}

		public static void PurgeExperimentInfos()
		{
			experiments.Clear();
		}

		public static string Generate_subject_id(string experiment_id, Vessel v, ExperimentSituation sit)
		{
			string sitStr = sit.ToString();
			if(!string.IsNullOrEmpty(sitStr))
			{
				if (sit.BiomeIsRelevant(Experiment(experiment_id)))
					sitStr += ScienceUtil.GetExperimentBiome(v.mainBody, v.latitude, v.longitude);
			}

			// generate subject id
			return Lib.BuildString(experiment_id, "@", v.mainBody.name, sitStr);
		}

		public static string Generate_subject(string experiment_id, string subject_id, Vessel v)
		{
			// in sandbox, do nothing else
			if (ResearchAndDevelopment.Instance == null) return subject_id;

			// if the subject id was never added to RnD
			if (ResearchAndDevelopment.GetSubjectByID(subject_id) == null)
			{
				// get subjects container using reflection
				// - we tried just changing the subject.id instead, and
				//   it worked but the new id was obviously used only after
				//   putting RnD through a serialization->deserialization cycle
				var subjects = Lib.ReflectionValue<Dictionary<string, ScienceSubject>>
				(
				  ResearchAndDevelopment.Instance,
				  "scienceSubjects"
				);

				var experiment = ResearchAndDevelopment.GetExperiment(experiment_id);
				var sit = GetExperimentSituation(v);
				var biome = ScienceUtil.GetExperimentBiome(v.mainBody, v.latitude, v.longitude);
				float multiplier = sit.Multiplier(Experiment(experiment_id));
				var cap = multiplier * experiment.baseValue;

				// create new subject
				ScienceSubject subject = new ScienceSubject
				(
				  		subject_id,
						Lib.BuildString(experiment.experimentTitle, " (", Lib.SpacesOnCaps(sit + biome), ")"),
						experiment.dataScale,
				  		multiplier,
						cap
				);

				// add it to RnD
				subjects.Add(subject_id, subject);
			}

			return subject_id;
		}

		public static string TestRequirements(string subject_id, string experiment_id, string requirements, Vessel v, ExperimentSituation sit)
		{
			CelestialBody body = v.mainBody;
			VesselData vd = v.KerbalismData();

			List<string> list = Lib.Tokenize(requirements, ',');
			foreach (string s in list)
			{
				var parts = Lib.Tokenize(s, ':');

				var condition = parts[0];
				string value = string.Empty;
				if(parts.Count > 1) value = parts[1];

				bool good = true;
				switch (condition)
				{
					case "OrbitMinInclination": good = Math.Abs(v.orbit.inclination) >= double.Parse(value); break;
					case "OrbitMaxInclination": good = Math.Abs(v.orbit.inclination) <= double.Parse(value); break;
					case "OrbitMinEccentricity": good = v.orbit.eccentricity >= double.Parse(value); break;
					case "OrbitMaxEccentricity": good = v.orbit.eccentricity <= double.Parse(value); break;
					case "OrbitMinArgOfPeriapsis": good = v.orbit.argumentOfPeriapsis >= double.Parse(value); break;
					case "OrbitMaxArgOfPeriapsis": good = v.orbit.argumentOfPeriapsis <= double.Parse(value); break;

					case "TemperatureMin": good = vd.EnvTemperature >= double.Parse(value); break;
					case "TemperatureMax": good = vd.EnvTemperature <= double.Parse(value); break;
					case "AltitudeMin": good = v.altitude >= double.Parse(value); break;
					case "AltitudeMax": good = v.altitude <= double.Parse(value); break;
					case "RadiationMin": good = vd.EnvRadiation >= double.Parse(value); break;
					case "RadiationMax": good = vd.EnvRadiation <= double.Parse(value); break;
					case "Microgravity": good = vd.EnvZeroG; break;
					case "Body": good = TestBody(v.mainBody.name, value); break;
					case "Shadow": good = vd.EnvInFullShadow; break;
					case "Sunlight": good = vd.EnvInSunlight; break;
					case "CrewMin": good = vd.CrewCount >= int.Parse(value); break;
					case "CrewMax": good = vd.CrewCount <= int.Parse(value); break;
					case "CrewCapacityMin": good = vd.CrewCapacity >= int.Parse(value); break;
					case "CrewCapacityMax": good = vd.CrewCapacity <= int.Parse(value); break;
					case "VolumePerCrewMin": good = vd.VolumePerCrew >= double.Parse(value); break;
					case "VolumePerCrewMax": good = vd.VolumePerCrew <= double.Parse(value); break;
					case "Greenhouse": good = vd.Greenhouses.Count > 0; break;
					case "Surface": good = Lib.Landed(v); break;
					case "Atmosphere": good = body.atmosphere && v.altitude < body.atmosphereDepth; break;
					case "AtmosphereBody": good = body.atmosphere; break;
					case "AtmosphereAltMin": good = body.atmosphere && (v.altitude / body.atmosphereDepth) >= double.Parse(value); break;
					case "AtmosphereAltMax": good = body.atmosphere && (v.altitude / body.atmosphereDepth) <= double.Parse(value); break;

					case "BodyWithAtmosphere": good = body.atmosphere; break;
					case "BodyWithoutAtmosphere": good = !body.atmosphere; break;
						
					case "SunAngleMin": good = vd.EnvSunBodyAngle >= double.Parse(value); break;
					case "SunAngleMax": good = vd.EnvSunBodyAngle <= double.Parse(value); break;

					case "Vacuum": good = !body.atmosphere || v.altitude > body.atmosphereDepth; break;
					case "Ocean": good = body.ocean && v.altitude < 0.0; break;
					case "PlanetarySpace": good = !Lib.IsSun(body) && !Lib.Landed(v) && v.altitude > body.atmosphereDepth; break;
					case "AbsoluteZero": good = vd.EnvTemperature < 30.0; break;
					case "InnerBelt": good = vd.EnvInnerBelt; break;
					case "OuterBelt": good = vd.EnvOuterBelt; break;
					case "MagneticBelt": good = vd.EnvInnerBelt || vd.EnvOuterBelt; break;
					case "Magnetosphere": good = vd.EnvMagnetosphere; break;
					case "Thermosphere": good = vd.EnvThermosphere; break;
					case "Exosphere": good = vd.EnvExosphere; break;
					case "InterPlanetary": good = Lib.IsSun(body) && !vd.EnvInterstellar; break;
					case "InterStellar": good = Lib.IsSun(body) && vd.EnvInterstellar; break;

					case "SurfaceSpeedMin": good = v.srfSpeed >= double.Parse(value); break;
					case "SurfaceSpeedMax": good = v.srfSpeed <= double.Parse(value); break;
					case "VerticalSpeedMin": good = v.verticalSpeed >= double.Parse(value); break;
					case "VerticalSpeedMax": good = v.verticalSpeed <= double.Parse(value); break;
					case "SpeedMin": good = v.speed >= double.Parse(value); break;
					case "SpeedMax": good = v.speed <= double.Parse(value); break;
					case "DynamicPressureMin": good = v.dynamicPressurekPa >= double.Parse(value); break;
					case "DynamicPressureMax": good = v.dynamicPressurekPa <= double.Parse(value); break;
					case "StaticPressureMin": good = v.staticPressurekPa >= double.Parse(value); break;
					case "StaticPressureMax": good = v.staticPressurekPa <= double.Parse(value); break;
					case "AtmDensityMin": good = v.atmDensity >= double.Parse(value); break;
					case "AtmDensityMax": good = v.atmDensity <= double.Parse(value); break;
					case "AltAboveGroundMin": good = v.heightFromTerrain >= double.Parse(value); break;
					case "AltAboveGroundMax": good = v.heightFromTerrain <= double.Parse(value); break;

					case "Part": good = Lib.HasPart(v, value); break;
					case "Module": good = Lib.FindModules(v.protoVessel, value).Count > 0; break;
						
					case "AstronautComplexLevelMin":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex) >= (double.Parse(value) - 1) / 2.0;
						break;
					case "AstronautComplexLevelMax":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex) <= (double.Parse(value) - 1) / 2.0;
						break;

					case "TrackingStationLevelMin":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.TrackingStation) >= (double.Parse(value) - 1) / 2.0;
						break;
					case "TrackingStationLevelMax":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.TrackingStation) <= (double.Parse(value) - 1) / 2.0;
						break;

					case "MissionControlLevelMin":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl) >= (double.Parse(value) - 1) / 2.0;
						break;
					case "MissionControlLevelMax":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.MissionControl) <= (double.Parse(value) - 1) / 2.0;
						break;

					case "AdministrationLevelMin":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.Administration) >= (double.Parse(value) - 1) / 2.0;
						break;
					case "AdministrationLevelMax":
						good = (ScenarioUpgradeableFacilities.Instance == null) || ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.Administration) <= (double.Parse(value) - 1) / 2.0;
						break;

					case "MaxAsteroidDistance": good = AsteroidDistance(v) <= double.Parse(value); break;
				}

				if (!good)
					return s;
			}
			
			if (!v.loaded && sit.AtmosphericFlight())
				return "Background flight";
				
			if (!sit.IsAvailable(Experiment(experiment_id)))
				return Lib.BuildString("Situation ", sit.Situation.ToString(), " invalid");

			// At this point we know the situation is valid and the experiment can be done
			// create it in R&D
			Generate_subject(experiment_id, subject_id, v);

			return string.Empty;
		}

		public static ExperimentSituation GetExperimentSituation(Vessel v)
		{
			return new ExperimentSituation(v);
		}

		private static bool TestBody(string bodyName, string requirement)
		{
			foreach(string s in Lib.Tokenize(requirement, ';'))
			{
				if (s == bodyName) return true;
				if(s[0] == '!' && s.Substring(1) == bodyName) return false;
			}
			return false;
		}

		private static double AsteroidDistance(Vessel vessel)
		{
			var target = vessel.targetObject;
			var vesselPosition = Lib.VesselPosition(vessel);

			// while there is a target, only consider the targeted vessel
			if(!vessel.loaded || target != null)
			{
				// asteroid MUST be the target if vessel is unloaded
				if (target == null) return double.MaxValue;

				var targetVessel = target.GetVessel();
				if (targetVessel == null) return double.MaxValue;

				if (targetVessel.vesselType != VesselType.SpaceObject) return double.MaxValue;

				// this assumes that all vessels of type space object are asteroids.
				// should be a safe bet unless Squad introduces alien UFOs.
				var asteroidPosition = Lib.VesselPosition(targetVessel);
				return Vector3d.Distance(vesselPosition, asteroidPosition);
			}

			// there's no target and vessel is not unloaded
			// look for nearby asteroids
			double result = double.MaxValue;
			foreach(Vessel v in FlightGlobals.VesselsLoaded)
			{
				if (v.vesselType != VesselType.SpaceObject) continue;
				var asteroidPosition = Lib.VesselPosition(v);
				double distance = Vector3d.Distance(vesselPosition, asteroidPosition);
				if (distance < result) result = distance;
			}
			return result;
		}

		public static string RequirementText(string requirement)
		{
			var parts = Lib.Tokenize(requirement, ':');

			var condition = parts[0];
			string value = string.Empty;
			if (parts.Count > 1) value = parts[1];
						
			switch (condition)
			{
				case "OrbitMinInclination": return Lib.BuildString("Min. inclination ", value, "°");
				case "OrbitMaxInclination": return Lib.BuildString("Max. inclination ", value, "°");
				case "OrbitMinEccentricity": return Lib.BuildString("Min. eccentricity ", value);
				case "OrbitMaxEccentricity": return Lib.BuildString("Max. eccentricity ", value);
				case "OrbitMinArgOfPeriapsis": return Lib.BuildString("Min. argument of Pe ", value);
				case "OrbitMaxArgOfPeriapsis": return Lib.BuildString("Max. argument of Pe ", value);
				case "AltitudeMin": return Lib.BuildString("Min. altitude ", Lib.HumanReadableRange(Double.Parse(value)));
				case "AltitudeMax":
					var v = Double.Parse(value);
					if (v >= 0) return Lib.BuildString("Max. altitude ", Lib.HumanReadableRange(v));
					return Lib.BuildString("Min. depth ", Lib.HumanReadableRange(-v));
				case "RadiationMin": return Lib.BuildString("Min. radiation ", Lib.HumanReadableRadiation(Double.Parse(value)));
				case "RadiationMax": return Lib.BuildString("Max. radiation ", Lib.HumanReadableRadiation(Double.Parse(value)));
				case "Body": return PrettyBodyText(value);
				case "TemperatureMin": return Lib.BuildString("Min. temperature ", Lib.HumanReadableTemp(Double.Parse(value)));
				case "TemperatureMax": return Lib.BuildString("Max. temperature ", Lib.HumanReadableTemp(Double.Parse(value)));
				case "CrewMin": return Lib.BuildString("Min. crew ", value);
				case "CrewMax": return Lib.BuildString("Max. crew ", value);
				case "CrewCapacityMin": return Lib.BuildString("Min. crew capacity ", value);
				case "CrewCapacityMax": return Lib.BuildString("Max. crew capacity ", value);
				case "VolumePerCrewMin": return Lib.BuildString("Min. vol./crew ", Lib.HumanReadableVolume(double.Parse(value)));
				case "VolumePerCrewMax": return Lib.BuildString("Max. vol./crew ", Lib.HumanReadableVolume(double.Parse(value)));
				case "MaxAsteroidDistance": return Lib.BuildString("Max. asteroid distance ", Lib.HumanReadableRange(double.Parse(value)));

				case "SunAngleMin": return Lib.BuildString("Min. sun angle ", Lib.HumanReadableAngle(double.Parse(value)));
				case "SunAngleMax": return Lib.BuildString("Max. sun angle ", Lib.HumanReadableAngle(double.Parse(value)));
					
				case "AtmosphereBody": return "Body with atmosphere";
				case "AtmosphereAltMin": return Lib.BuildString("Min. atmosphere altitude ", value);
				case "AtmosphereAltMax": return Lib.BuildString("Max. atmosphere altitude ", value);
					
				case "SurfaceSpeedMin": return Lib.BuildString("Min. surface speed ", Lib.HumanReadableSpeed(double.Parse(value)));
				case "SurfaceSpeedMax": return Lib.BuildString("Max. surface speed ", Lib.HumanReadableSpeed(double.Parse(value)));
				case "VerticalSpeedMin": return Lib.BuildString("Min. vertical speed ", Lib.HumanReadableSpeed(double.Parse(value)));
				case "VerticalSpeedMax": return Lib.BuildString("Max. vertical speed ", Lib.HumanReadableSpeed(double.Parse(value)));
				case "SpeedMin": return Lib.BuildString("Min. speed ", Lib.HumanReadableSpeed(double.Parse(value)));
				case "SpeedMax": return Lib.BuildString("Max. speed ", Lib.HumanReadableSpeed(double.Parse(value)));
				case "DynamicPressureMin": return Lib.BuildString("Min. dynamic pressure ", Lib.HumanReadablePressure(double.Parse(value)));
				case "DynamicPressureMax": return Lib.BuildString("Max. dynamic pressure ", Lib.HumanReadablePressure(double.Parse(value)));
				case "StaticPressureMin": return Lib.BuildString("Min. pressure ", Lib.HumanReadablePressure(double.Parse(value)));
				case "StaticPressureMax": return Lib.BuildString("Max. pressure ", Lib.HumanReadablePressure(double.Parse(value)));
				case "AtmDensityMin": return Lib.BuildString("Min. atm. density ", Lib.HumanReadablePressure(double.Parse(value)));
				case "AtmDensityMax": return Lib.BuildString("Max. atm. density ", Lib.HumanReadablePressure(double.Parse(value)));
				case "AltAboveGroundMin": return Lib.BuildString("Min. ground altitude ", Lib.HumanReadableRange(double.Parse(value)));
				case "AltAboveGroundMax": return Lib.BuildString("Max. ground altitude ", Lib.HumanReadableRange(double.Parse(value)));

				case "MissionControlLevelMin": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.MissionControl), " level ", value);
				case "MissionControlLevelMax": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.MissionControl), " max. level ", value);
				case "AdministrationLevelMin": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.Administration), " level ", value);
				case "AdministrationLevelMax": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.Administration), " max. level ", value);
				case "TrackingStationLevelMin": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.TrackingStation), " level ", value);
				case "TrackingStationLevelMax": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.TrackingStation), " max. level ", value);
				case "AstronautComplexLevelMin": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.AstronautComplex), " level ", value);
				case "AstronautComplexLevelMax": return Lib.BuildString(ScenarioUpgradeableFacilities.GetFacilityName(SpaceCenterFacility.AstronautComplex), " max. level ", value);

				case "Part": return Lib.BuildString("Needs part ", value);
				case "Module": return Lib.BuildString("Needs module ", value);

				default:
					return Lib.SpacesOnCaps(condition);
			}
		}

		public static string PrettyBodyText(string requires)
		{
			string result = "";
			foreach(var s in Lib.Tokenize(requires, ';'))
			{
				if (result.Length > 0) result += ", ";
				if (s[0] == '!') result += "not " + s.Substring(1);
				else result += s;
			}
			return result;
		}

		/// <summary>
		/// Return the result description (Experiment definition RESULTS node) for the subject_id.
		/// Same as the stock ResearchAndDevelopment.GetResults(subject_id) but can be forced to return a non-randomized result
		/// </summary>
		/// <param name="randomized">If true the result can be different each this is called</param>
		/// <param name="useGenericIfNotFound">If true, a generic text will be returned if no RESULTS{} definition exists</param>
		public static string SubjectResultDescription(string subject_id, bool useGenericIfNotFound = true)
		{
			string result = ResearchAndDevelopment.GetResults(subject_id);
			if (result == null) result = string.Empty;
			if (result == string.Empty && useGenericIfNotFound)
			{
				result = Lib.TextVariant(
					  "Our researchers will jump on it right now",
					  "This cause some excitement",
					  "These results are causing a brouhaha in R&D",
					  "Our scientists look very confused",
					  "The scientists won't believe these readings");
			}
			return result;
		}

		public static void RegisterSampleMass(string experiment_id, double sampleMass)
		{
			// get experiment id out of subject id
			int i = experiment_id.IndexOf('@');
			var id = i > 0 ? experiment_id.Substring(0, i) : experiment_id;

			if (sampleMasses.ContainsKey(id))
			{
				if (Math.Abs(sampleMasses[id] - sampleMass) > double.Epsilon)
					Lib.Log("Science Warning: different sample masses for Experiment " + id + " defined.");
			}
			else
			{
				sampleMasses.Add(id, sampleMass);
				Lib.Log("Science: registered sample mass for " + id + ": " + sampleMass.ToString("F3"));
			}
		}

		public static double GetSampleMass(string experiment_id)
		{
			// get experiment id out of subject id
			int i = experiment_id.IndexOf('@');
			var id = i > 0 ? experiment_id.Substring(0, i) : experiment_id;

			if (!sampleMasses.ContainsKey(id)) return 0;
			return sampleMasses[id];
		}

		// experiment info cache
		static readonly Dictionary<string, ExperimentInfo> experiments = new Dictionary<string, ExperimentInfo>();
		static readonly Dictionary<string, double> sampleMasses = new Dictionary<string, double>();
		static readonly List<XmitFile> xmitFiles = new List<XmitFile>();

		private class DeferredCreditValues {
			internal string subject_id;
			internal double size;
			internal ProtoVessel pv;

			public DeferredCreditValues(string subject_id, double size, ProtoVessel pv)
			{
				this.subject_id = subject_id;
				this.size = size;
				this.pv = pv;
			}
		}
	}

} // KERBALISM

