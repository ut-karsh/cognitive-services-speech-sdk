// <copyright file="DatabaseConnector.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE.md file in the project root for full license information.
// </copyright>

namespace Connector
{
    using System;
    using System.Collections.Generic;
    using System.Data.SqlClient;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;

    public class DatabaseConnector : IDisposable
    {
        private readonly ILogger logger;

        private readonly string databaseConnectionString;

        private SqlConnection connection;

        public DatabaseConnector(ILogger logger, string databaseConnectionString)
        {
            this.logger = logger;
            this.databaseConnectionString = databaseConnectionString;
        }

        public async Task<bool> StoreTranscriptionAsync(
            Guid transcriptionId,
            string locale,
            string fileName,
            float approximateCost,
            SpeechTranscript speechTranscript)
        {
            if (speechTranscript == null)
            {
                throw new ArgumentNullException(nameof(speechTranscript));
            }

            try
            {
                this.connection = new SqlConnection(this.databaseConnectionString);
                this.connection.Open();

                var query = "INSERT INTO dbo.Transcriptions (ID, Locale, Name, Source, Timestamp, Duration, DurationInSeconds, NumberOfChannels, ApproximateCost)" +
                    " VALUES (@id, @locale, @name, @source, @timestamp, @duration, @durationInSeconds, @numberOfChannels, @approximateCost)";

                using (var command = new SqlCommand(query, this.connection))
                {
                    command.Parameters.AddWithValue("@id", transcriptionId);
                    command.Parameters.AddWithValue("@locale", locale);
                    command.Parameters.AddWithValue("@name", fileName);
                    command.Parameters.AddWithValue("@source", speechTranscript.Source);
                    command.Parameters.AddWithValue("@timestamp", speechTranscript.Timestamp);
                    command.Parameters.AddWithValue("@duration", speechTranscript.Duration ?? string.Empty);
                    command.Parameters.AddWithValue("@durationInSeconds", TimeSpan.FromTicks(speechTranscript.DurationInTicks).TotalSeconds);
                    command.Parameters.AddWithValue("@numberOfChannels", speechTranscript.CombinedRecognizedPhrases.Count());
                    command.Parameters.AddWithValue("@approximateCost", approximateCost);

                    var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

                    if (result < 0)
                    {
                        this.logger.LogInformation("Did not store json in Db, command did not update table");
                    }
                    else
                    {
                        var phrasesByChannel = speechTranscript.RecognizedPhrases.GroupBy(t => t.Channel);

                        foreach (var phrases in phrasesByChannel)
                        {
                            var channel = phrases.Key;
                            await this.StoreCombinedRecognizedPhrasesAsync(transcriptionId, channel, speechTranscript, phrases).ConfigureAwait(false);
                        }
                    }
                }

                this.connection.Close();
            }
            catch (SqlException e)
            {
                this.logger.LogInformation(e.ToString());
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            this.logger.LogInformation("Disposing DBConnector");
            if (disposing)
            {
                this.connection?.Dispose();
            }
        }

        private async Task StoreCombinedRecognizedPhrasesAsync(Guid transcriptionId, int channel, SpeechTranscript speechTranscript, IEnumerable<RecognizedPhrase> recognizedPhrases)
        {
            var combinedRecognizedPhraseID = Guid.NewGuid();

            var combinedPhrases = speechTranscript.CombinedRecognizedPhrases.Where(t => t.Channel == channel).FirstOrDefault();

            var query = "INSERT INTO dbo.CombinedRecognizedPhrases (ID, TranscriptionID, Channel, Lexical, Itn, MaskedItn, Display, SentimentPositive, SentimentNeutral, SentimentNegative)" +
                " VALUES (@id, @transcriptionID, @channel, @lexical, @itn, @maskedItn, @display, @sentimentPositive, @sentimentNeutral, @sentimentNegative)";

            using var command = new SqlCommand(query, this.connection);
            command.Parameters.AddWithValue("@id", combinedRecognizedPhraseID);
            command.Parameters.AddWithValue("@transcriptionID", transcriptionId);
            command.Parameters.AddWithValue("@channel", channel);

            command.Parameters.AddWithValue("@lexical", combinedPhrases?.Lexical ?? string.Empty);
            command.Parameters.AddWithValue("@itn", combinedPhrases?.ITN ?? string.Empty);
            command.Parameters.AddWithValue("@maskedItn", combinedPhrases?.MaskedITN ?? string.Empty);
            command.Parameters.AddWithValue("@display", combinedPhrases?.Display ?? string.Empty);

            command.Parameters.AddWithValue("@sentimentPositive", combinedPhrases?.Sentiment?.Positive ?? 0f);
            command.Parameters.AddWithValue("@sentimentNeutral", combinedPhrases?.Sentiment?.Neutral ?? 0f);
            command.Parameters.AddWithValue("@sentimentNegative", combinedPhrases?.Sentiment?.Negative ?? 0f);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (result < 0)
            {
                this.logger.LogInformation("Did not store combined phrase in Db, command did not update table");
            }
            else
            {
                var orderedPhrases = recognizedPhrases.OrderBy(p => p.OffsetInTicks);
                var previousEndInMs = 0.0;
                foreach (var phrase in orderedPhrases)
                {
                    await this.StoreRecognizedPhraseAsync(combinedRecognizedPhraseID, phrase, previousEndInMs).ConfigureAwait(false);
                    previousEndInMs = (TimeSpan.FromTicks(phrase.OffsetInTicks) + TimeSpan.FromTicks(phrase.DurationInTicks)).TotalMilliseconds;
                }
            }
        }

        private async Task StoreRecognizedPhraseAsync(Guid combinedPhraseID, RecognizedPhrase recognizedPhrase, double previousEndInMs)
        {
            var silenceBetweenCurrentAndPreviousSegmentInMs = Math.Max(0, TimeSpan.FromTicks(recognizedPhrase.OffsetInTicks).TotalMilliseconds - previousEndInMs);

            var phraseId = Guid.NewGuid();
            var query = "INSERT INTO dbo.RecognizedPhrases (ID, CombinedRecognizedPhraseID, RecognitionStatus, Speaker, Channel, Offset, Duration, SilenceBetweenCurrentAndPreviousSegmentInMs)" +
                " VALUES (@id, @combinedRecognizedPhraseID, @recognitionStatus, @speaker, @channel, @offset, @duration, @silenceBetweenCurrentAndPreviousSegmentInMs)";

            using var command = new SqlCommand(query, this.connection);
            command.Parameters.AddWithValue("@id", phraseId);
            command.Parameters.AddWithValue("@combinedRecognizedPhraseID", combinedPhraseID);
            command.Parameters.AddWithValue("@recognitionStatus", recognizedPhrase.RecognitionStatus);
            command.Parameters.AddWithValue("@speaker", recognizedPhrase.Speaker);
            command.Parameters.AddWithValue("@channel", recognizedPhrase.Channel);
            command.Parameters.AddWithValue("@offset", recognizedPhrase.Offset);
            command.Parameters.AddWithValue("@duration", recognizedPhrase.Duration);
            command.Parameters.AddWithValue("@silenceBetweenCurrentAndPreviousSegmentInMs", silenceBetweenCurrentAndPreviousSegmentInMs);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (result < 0)
            {
                this.logger.LogInformation("Did not store phrase in Db, command did not update table");
            }
            else
            {
                foreach (var nbestResult in recognizedPhrase.NBest)
                {
                    await this.StoreNBestAsync(phraseId, nbestResult).ConfigureAwait(false);
                }
            }
        }

        private async Task StoreNBestAsync(Guid recognizedPhraseID, NBest nbest)
        {
            var nbestID = Guid.NewGuid();
            var query = "INSERT INTO dbo.NBests (ID, RecognizedPhraseID, Confidence, Lexical, Itn, MaskedItn, Display, SentimentNegative, SentimentNeutral, SentimentPositive)" +
                " VALUES (@id, @recognizedPhraseID, @confidence, @lexical, @itn, @maskedItn, @display, @sentimentNegative, @sentimentNeutral, @sentimentPositive)";

            using var command = new SqlCommand(query, this.connection);
            command.Parameters.AddWithValue("@id", nbestID);
            command.Parameters.AddWithValue("@recognizedPhraseID", recognizedPhraseID);
            command.Parameters.AddWithValue("@confidence", nbest.Confidence);
            command.Parameters.AddWithValue("@lexical", nbest.Lexical);
            command.Parameters.AddWithValue("@itn", nbest.ITN);
            command.Parameters.AddWithValue("@maskedItn", nbest.MaskedITN);
            command.Parameters.AddWithValue("@display", nbest.Display);

            command.Parameters.AddWithValue("@sentimentNegative", nbest?.Sentiment?.Negative ?? 0f);
            command.Parameters.AddWithValue("@sentimentNeutral", nbest?.Sentiment?.Neutral ?? 0f);
            command.Parameters.AddWithValue("@sentimentPositive", nbest?.Sentiment?.Positive ?? 0f);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            if (result < 0)
            {
                this.logger.LogInformation("Did not store nbest in Db, command did not update table");
            }
            else
            {
                if (nbest.Words == null)
                {
                    return;
                }

                foreach (var word in nbest.Words)
                {
                    await this.StoreWordsAsync(nbestID, word).ConfigureAwait(false);
                }
            }
        }

        private async Task StoreWordsAsync(Guid nbestId, Words word)
        {
            var wordID = Guid.NewGuid();
            var query = "INSERT INTO dbo.Words (ID, NBestID, Word, Offset, Duration, Confidence)" +
                " VALUES (@id, @nBestID, @word, @offset, @duration, @confidence)";

            using var command = new SqlCommand(query, this.connection);
            command.Parameters.AddWithValue("@id", wordID);
            command.Parameters.AddWithValue("@nBestID", nbestId);
            command.Parameters.AddWithValue("@word", word.Word);
            command.Parameters.AddWithValue("@offset", word.Offset);
            command.Parameters.AddWithValue("@duration", word.Duration);
            command.Parameters.AddWithValue("@confidence", word.Confidence);

            var result = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            if (result < 0)
            {
                this.logger.LogInformation("Did not Store word result in Db, command did not update table");
            }
        }
    }
}