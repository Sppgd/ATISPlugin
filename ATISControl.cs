﻿using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using vatsys;

namespace ATISPlugin
{
    public class ATISControl
    {
        public int Number { get; private set; }
        private int Index => Number - 1;
        private string Callsign { get; set; }
        public char ID { get; set; } = 'Z';
        public bool IsZulu { get; set; }
        public string ICAO { get; set; }
        private Coordinate VisPoint { get; set; }
        public string FrequencyDisplay { get; set; }
        private uint Frequency { get; set; }
        private uint AliasFrequency { get; set; }
        private int FSDFrequency => AliasFrequency != 199998000U ? VSCSFrequencyToFSDFrequency(AliasFrequency) : VSCSFrequencyToFSDFrequency(Frequency);

        public bool Broadcasting { get; set; }
        public bool Listening { get; set; }
        public bool CanListen => !string.IsNullOrWhiteSpace(FileName);
        public string FileName { get; set; }
        public DateTime DateTimeUtc { get; set; }
        public bool TimeCheck { get; set; } = true;
        public List<ATISLine> Lines { get; set; } = new List<ATISLine>();
        private double ATISDuration { get; set; }
        private MemoryStream ATISStream;
        private MemoryStream TimeCheckStream;
        private double CompleteATISDuration { get; set; }
        private MemoryStream CompleteStream;
        public List<ATISLine> SuggestedLines { get; set; } = new List<ATISLine>();
        public bool HasUpdates => SuggestedLines.Any();
        public SpeechSynthesizer SpeechSynth { get; set; }
        private PromptBuilder ATISSpoken { get; set; }
        private SpeechAudioFormatInfo SpeechFormat { get; set; }
        public string METARRaw { get; set; }
        public string METARLastRaw { get; set; }
        private WaveFormat WaveForm { get; set; } = new WaveFormat(44100, 1);
        public PromptRate PromptRate { get; set; } = PromptRate.Medium;
        public InstalledVoice InstalledVoice { get; set; }
        public SoundPlayer SoundPlayer { get; set; } = new SoundPlayer();
        private CultureInfo CultureInfo => CultureInfo.GetCultureInfo("en");    // TODO: Check if in the ATIS.xml

        public event EventHandler StatusChanged;
        private readonly Timer LoopTimer;

        public ATISControl()
        {
            foreach (var line in Plugin.ATISData.Editor)
            {
                var atisLine = new ATISLine(line.name, line.InputType, line.NameIsSpoken, line.NumbersSpokenGrouped, line.value, METARField.None);

                switch (atisLine.Name)
                {
                    case "WIND":
                        atisLine.METARField = METARField.Wind;
                        break;
                    case "VIS":
                        atisLine.METARField = METARField.Visibility;
                        break;
                    case "WX":
                        atisLine.METARField = METARField.Weather;
                        break;
                    case "CLD":
                        atisLine.METARField = METARField.Cloud;
                        break;
                    case "TMP":
                        atisLine.METARField = METARField.Temperature;
                        break;
                    case "QNH":
                        atisLine.METARField = METARField.QNH;
                        break;
                }

                Lines.Add(atisLine);
            }

            Lines.Add(new ATISLine("ZULU"));

            LoopTimer = new Timer
            {
                AutoReset = false
            };
            LoopTimer.Elapsed += new ElapsedEventHandler(LoopTimer_Elapsed);

            SpeechSynth = new SpeechSynthesizer()
            {
                Rate = 0
            };
            SpeechFormat = new SpeechAudioFormatInfo(WaveForm.SampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono);

            InstalledVoice = SpeechSynth.GetInstalledVoices().FirstOrDefault();
        }

        public ATISControl(int number) : this()
        {
            Number = number;
        }

        private void LoopTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            if (!Broadcasting) return;

            BroadcastStart();
        }

        public async Task Create(string icao, string frequency, string coordinates)
        {
            if (!Network.IsConnected || !Network.IsValidATC) return;

            if (Network.GetATISConnected(Index)) return;

            try
            {
                ICAO = icao;
                FrequencyDisplay = Normalize25KhzFrequency(frequency);
                Frequency = Normalize25KhzFrequency(FrequencyToUInt(frequency));
                AliasFrequency = Normalize25KhzFrequency(199998000U);
                Callsign = $"{icao}_ATIS";
                VisPoint = new Coordinate(coordinates);
                IsZulu = false;

                Network.ConnectATIS(Index, Callsign, ICAO, FSDFrequency, VisPoint);
            }
            catch (Exception ex)
            {
                Errors.Add(new Exception($"Could not create ATIS: {ex.Message}"), Plugin.DisplayName);

                await Delete();
            }

            try
            {
                MMI.OpenATISWindow(Callsign);
            }
            catch { }

            StatusChanged?.Invoke(this, null);
        }

        public async Task Delete(bool killOnNetwork = true)
        {
            await BroadcastStop(killOnNetwork);

            if (killOnNetwork && Network.GetATISConnected(Index))
            {
                Network.DisconnectATIS(Index);
            }

            ICAO = null;
            ID = 'Z'; 
            FrequencyDisplay = null;
            Frequency = 0;
            AliasFrequency = 0;
            Callsign = null;
            VisPoint = null;
            IsZulu = false;

            METARLastRaw = null;
            METARRaw = null;

            foreach (var line in Lines)
            {
                line.Value = null;
                line.Changed = false;
            }

            SuggestedLines.Clear();

            SoundPlayer.Stop();

            StatusChanged?.Invoke(this, null);
        }

        public async Task Save(char id, Dictionary<string, string> items, bool timeCheck)
        {
            await BroadcastStop();

            DateTimeUtc = DateTime.UtcNow;
            ID = id;
            TimeCheck = timeCheck;

            foreach (var line in Lines)
            {
                line.Changed = false;
            }

            foreach (var item in items)
            {
                var line = Lines.FirstOrDefault(x => x.Name == item.Key);

                if (line == null) continue;

                if (line.Value == item.Value) continue;

                line.Value = item.Value.ToUpper();

                line.Changed = true;
            }

            SuggestedLines.Clear();

            GenerateSpoken();

            ATISStream = new MemoryStream();

            ATISDuration = SetContent(ATISSpoken, ref ATISStream);

            GenerateFile();

            Listening = true;

            StatusChanged?.Invoke(this, null);
        }

        public void ListenStart()
        {
            if (!CanListen) return;

            Listening = true;

            var directory = Path.Combine(Plugin.DatasetPath, "Temp");

            var file = Path.Combine(directory, FileName);

            SoundPlayer.SoundLocation = file;

            SoundPlayer.Play();
        }

        public void ListenStop()
        {
            Listening = false;

            SoundPlayer.Stop();
        }

        public void BroadcastStart()
        {
            Broadcasting = true;

            CompleteATISDuration = GenerateCompleteStream();

            Network.UpdateATIS(Index, ID, GetInfo());

            var audio = ReadMemoryStream(CompleteStream);

            var duration = TimeCheck ? TimeSpan.FromMilliseconds(CompleteATISDuration + 60000.0) : TimeSpan.Zero;

            var atisAudio = new ATISAudio(audio, Index, Callsign, Frequency, VisPoint, duration);

            Plugin.ToBroadcast.Add(atisAudio);  

            if (!TimeCheck) return;

            LoopTimer.Interval = CompleteATISDuration;

            LoopTimer.Start();
        }

        public static byte[] ReadMemoryStream(Stream input)
        {
            input.Seek(0, SeekOrigin.Begin);

            using (MemoryStream ms = new MemoryStream())
            {
                input.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public async Task BroadcastStop(bool killOnNetwork = true)
        {
            Broadcasting = false;

            if (killOnNetwork)
            {
                try
                {
                    await AFV.RemoveATISBot(Index);
                }
                catch { }
            }
        }

        public string[] GetInfo()
        {
            var output = new List<string>
            {
                $"ATIS {ICAO} {ID} {DateTimeUtc:ddHHmm}"
            };
            if (IsZulu)
            {
                var zulu = Lines.FirstOrDefault(x => x.Name == "ZULU");
                if (zulu == null || string.IsNullOrWhiteSpace(zulu.Value)) return output.ToArray();
                output.Add(zulu.Value);
                return output.ToArray();
            }
            foreach (var line in Lines.Where(x => x.Visible).ToList())
            {
                if (line.Name == "OFCW_NOTIFY" || line.Name == "ZULU") continue;
                var change = " ";
                if (line.Changed) change = "+";
                output.Add($"{change}[{line.Name}] {line.Value}");
            }
            return output.ToArray();
        }

        public void SuggestionsCancel()
        {
            METARRaw = METARLastRaw;
            SuggestedLines.Clear();
        }

        public bool UpdateMetar(string metar)
        {
            if (ICAO == null) return false;

            if (metar == METARRaw) return false;

            if (IsZulu && !string.IsNullOrWhiteSpace(METARRaw)) return false;

            METARLastRaw = METARRaw;

            METARRaw = metar;

            var updatedLines = new METAR().Process(metar);

            var suggestedLines = new List<ATISLine>();

            foreach (var type in Enum.GetValues(typeof(METARField)).Cast<METARField>())
            {
                if (type == METARField.None) continue;

                var updatedLine = updatedLines.GetField(type);

                if (updatedLine == null) continue;

                var currentLine = Lines.FirstOrDefault(x => x.METARField == type);

                if (currentLine == null) continue;

                if (updatedLine == currentLine.Value) continue;

                var suggestLine = new ATISLine(currentLine.Name, currentLine.Type, currentLine.NameSpoken, currentLine.NumbersGrouped, updatedLine, currentLine.METARField);

                suggestedLines.Add(suggestLine);
            }

            SuggestedLines = suggestedLines;

            if (!SuggestedLines.Any()) return false;

            return true;
        }

        private PromptBuilder DoReplacements(PromptBuilder promptBuilder, string text, bool groupNumbers = false)
        {
            if (text == null) return null;

            var output = new List<string>();

            foreach (var word in Regex.Split(text, "\\s+"))
            {
                var input = word;

                foreach (var regexReplacement in Plugin.ATISData.Translations.Where(x => !string.IsNullOrWhiteSpace(x.Regex)))
                {
                    if (Regex.IsMatch(input, regexReplacement.Regex))
                        input = Regex.Replace(input, regexReplacement.Regex, regexReplacement.Spoken);
                }

                if (!groupNumbers)
                {
                    foreach (Match match in Regex.Matches(input, "(\\s|^|\\.|\\,)\\d+(\\s|$|\\.|\\,)").Cast<Match>())
                    {
                        string newValue = match.Value.Aggregate<char, string>(string.Empty, (Func<string, char, string>)((c, i) => i != ' ' ? c + i.ToString() + " " : c + i.ToString()));
                        input = input.Replace(match.Value, newValue);
                    }
                }
                else
                {
                    input = Regex.Replace(Regex.Replace(input, "(\\d{1,2})([0]{3})", "$1 thousand"), "(\\d{1,2})(\\d)([0]{2})", "$1 thousand $2 hundred");
                }

                foreach (var stringReplacement in Plugin.ATISData.Translations.Where(x => !string.IsNullOrWhiteSpace(x.String)))
                    input = Regex.Replace(input, "\\b" + Regex.Escape(stringReplacement.String) + "\\b", stringReplacement.Spoken);

                output.Add(input);
            }

            foreach (var word in output)
            {
                var phonemeReplacement = Plugin.ATISData.Translations.Where(x => !string.IsNullOrWhiteSpace(x.String) && !string.IsNullOrWhiteSpace(x.Alphabet)).FirstOrDefault(x => x.String == word);

                if (phonemeReplacement == null)
                {
                    promptBuilder.AppendText(word + " ");
                    continue;
                }

                if (phonemeReplacement.Alphabet != "Text" && !InstalledVoice.VoiceInfo.Name.Contains("Microsoft"))
                {
                    promptBuilder.AppendText(phonemeReplacement.FallbackSpoken + " ");
                }
                else
                {
                    var temp = $"<phoneme alphabet=";
                    switch (phonemeReplacement.Alphabet)
                    {
                        case "Text":
                            promptBuilder.AppendText(phonemeReplacement.Spoken + " ");
                            continue;
                        case "IPA":
                            promptBuilder.AppendTextWithPronunciation(word, phonemeReplacement.Spoken);
                            continue;
                        case "SAPI":
                            temp += "\"x-microsoft-sapi\"";
                            break;
                        case "UPS":
                            temp += "\"x-microsoft-ups\"";
                            break;
                    }
                    string ssmlMarkup = temp + " ph=\"" + phonemeReplacement.Spoken + "\">" + word + "</phoneme>";
                    promptBuilder.AppendSsmlMarkup(ssmlMarkup);
                }
            }

            return promptBuilder;
        }

        private double SetContent(PromptBuilder speech, ref MemoryStream stream)
        {
            if (SpeechSynth == null) throw new Exception("Text to speech not available.");
            SpeechSynth.SetOutputToAudioStream(stream, SpeechFormat);
            SpeechSynth.Speak(speech);
            SpeechSynth.SetOutputToNull();
            stream.Seek(0L, SeekOrigin.Begin);
            return stream.Length / WaveForm.AverageBytesPerSecond * 1000.0;
        }

        private void GenerateSpoken()
        {
            var speech = new PromptBuilder(CultureInfo);
            speech.StartVoice(InstalledVoice.VoiceInfo.Name);
            speech.StartStyle(new PromptStyle(PromptRate));
            speech.AppendBreak(TimeSpan.FromSeconds(0.5));

            speech.StartSentence();
            speech = DoReplacements(speech, $"{ICAO} ATIS {ID}", false);
            speech.EndSentence();
            speech.AppendBreak(TimeSpan.FromSeconds(1.0));

            if (IsZulu)
            {
                var zulu = Lines.FirstOrDefault(x => x.Name == "ZULU");
                if (zulu != null && zulu.Value != null)
                {
                    speech.StartSentence();
                    speech = DoReplacements(speech, zulu.Value);
                    speech.EndSentence();
                    speech.AppendBreak(TimeSpan.FromSeconds(1.0));
                }
            }
            else
            {
                foreach (var line in Lines)
                {
                    if (string.IsNullOrWhiteSpace(line.Value)) continue;

                    speech.StartSentence();

                    if (line.Name == "OFCW_NOTIFY")
                    {
                        speech = DoReplacements(speech, $"On first contact with {ICAO} {line.Value}, notify receipt of information {ID}", line.NumbersGrouped);
                    }
                    else
                    {
                        speech = DoReplacements(speech, line.NameSpoken ? $"{line.Name} {line.Value}" : line.Value, line.NumbersGrouped);
                    }

                    speech.EndSentence();
                    speech.AppendBreak(TimeSpan.FromSeconds(1.0));
                }
            }

            speech.EndStyle();
            speech.EndVoice();

            ATISSpoken = speech;
        }

        private void GenerateTimeCheck()
        {
            PromptBuilder speech = new PromptBuilder(CultureInfo);
            speech.StartVoice(InstalledVoice.VoiceInfo.Name);
            speech.StartStyle(new PromptStyle(PromptRate));
            speech.AppendBreak(TimeSpan.FromSeconds(3.0));
            speech.AppendText(CreateTimecheckText(TimeSpan.FromMilliseconds(ATISDuration) + TimeSpan.FromSeconds(7.0)));
            speech.EndStyle();
            speech.EndVoice();
            SetTimeCheckContent(speech);
        }

        private string CreateTimecheckText(TimeSpan offset)
        {
            DateTime nearest = (DateTime.UtcNow + offset).RoundToNearest(TimeSpan.FromSeconds(30.0));
            string timecheckText = "Time check, " + nearest.ToString("HHmm").Aggregate<char, string>(string.Empty, (Func<string, char, string>)((c, i) => c + i.ToString() + " "));
            if (nearest.Second == 30)
                timecheckText += " and a half.";
            return timecheckText;
        }

        private double SetTimeCheckContent(PromptBuilder speech)
        {
            TimeCheckStream = new MemoryStream();
            return SetContent(speech, ref TimeCheckStream);
        }

        private double GenerateCompleteStream()
        {
            CompleteStream = new MemoryStream();

            ATISStream.Seek(0, SeekOrigin.Begin);
            ATISStream.CopyTo(CompleteStream);

            CompleteStream.Seek(0, SeekOrigin.End);

            if (TimeCheck)
            {
                GenerateTimeCheck();
                TimeCheckStream.Seek(0, SeekOrigin.Begin);
                TimeCheckStream.CopyTo(CompleteStream);
            }

            return CompleteStream.Length / WaveForm.AverageBytesPerSecond * 1000.0;
        }

        private void GenerateFile()
        {
            FileName = $"{ICAO}_{ID}_{DateTime.UtcNow:HHmmss}.wav";

            var directory = Path.Combine(Plugin.DatasetPath, "Temp");

            var file = Path.Combine(directory, FileName);

            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            SpeechSynth.SetOutputToWaveFile(file, SpeechFormat);
            SpeechSynth.Speak(ATISSpoken);
            SpeechSynth.SetOutputToNull();

            ListenStart();
        }

        private static int VSCSFrequencyToFSDFrequency(uint freq) => (int)(freq / 1000U) - 100000;

        private static uint Normalize25KhzFrequency(uint freq) => freq < 100000000U || freq % 100000U != 20000U && freq % 100000U != 70000U ? freq : freq + 5000U;

        public static string Normalize25KhzFrequency(string freq)
        {
            if (freq.IndexOf('.') < 3)
                return freq;
            string str = Conversions.Normalize25KhzFrequency(Convert.ToUInt32(freq.Replace(".", "")) * (uint)Math.Pow(10.0, (double)(9 - (freq.Length - 1)))).ToString();
            freq = str.Substring(0, 3) + "." + str.Substring(3);
            return freq;
        }

        public static uint FrequencyToUInt(string freq)
        {
            int freq1 = (int)(uint)Math.Round(double.Parse(Normalize25KhzFrequency(freq.Trim())) * 1000000.0);
            CheckFrequencyValid((uint)freq1);
            return (uint)freq1;
        }

        public static void CheckFrequencyValid(uint freq)
        {
            switch (freq % 100000U)
            {
                case 0:
                    break;
                case 25000:
                    break;
                case 50000:
                    break;
                case 75000:
                    break;
                default:
                    throw new ArgumentException("8.33kHz frequencies not currently supported.");
            }
        }
    }
}
