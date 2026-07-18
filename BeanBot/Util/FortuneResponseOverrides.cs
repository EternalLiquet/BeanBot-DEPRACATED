#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace BeanBot.Util
{
    public static class FortuneResponseOverrides
    {
        public const string ShowerResponse = "Yes. Go take a shower.";

        private const int MaximumPhraseLength = 24;

        private static readonly HashSet<string> AdviceCues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "allowed",
            "avoid",
            "avoiding",
            "bad",
            "better",
            "can",
            "can't",
            "could",
            "couldn't",
            "delay",
            "delaying",
            "fine",
            "good",
            "may",
            "might",
            "mistake",
            "must",
            "mustn't",
            "necessary",
            "need",
            "needn't",
            "okay",
            "ok",
            "ought",
            "oughtn't",
            "postpone",
            "postponing",
            "recommend",
            "recommended",
            "refrain",
            "refraining",
            "required",
            "sensible",
            "shall",
            "should",
            "shouldn't",
            "skip",
            "skipping",
            "smart",
            "supposed",
            "time",
            "wise",
            "without",
            "worse",
            "wrong"
        };

        private static readonly HashSet<string> EvaluationCues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bad",
            "better",
            "fine",
            "good",
            "mistake",
            "necessary",
            "okay",
            "ok",
            "recommended",
            "sensible",
            "smart",
            "wise",
            "worse",
            "wrong"
        };

        private static readonly HashSet<string> DirectAdviceModals = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "can",
            "can't",
            "could",
            "couldn't",
            "may",
            "might",
            "must",
            "mustn't",
            "needn't",
            "ought",
            "oughtn't",
            "shall",
            "should",
            "shouldn't"
        };

        private static readonly HashSet<string> BathingActionIntroducers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "get",
            "getting",
            "got",
            "had",
            "have",
            "having",
            "take",
            "taking",
            "took"
        };

        private static readonly HashSet<string> BathingCoordinationWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "after",
            "and",
            "before",
            "then"
        };

        private static readonly HashSet<string> BathingActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bath",
            "bathe",
            "bathed",
            "bathes",
            "bathing",
            "baths",
            "shower",
            "showered",
            "showering",
            "showers",
            "unshowered"
        };

        private static readonly HashSet<string> AllowedBeforeAction = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a",
            "actually",
            "again",
            "allowed",
            "an",
            "and",
            "another",
            "anything",
            "avoid",
            "avoiding",
            "bad",
            "be",
            "being",
            "better",
            "can",
            "can't",
            "cold",
            "could",
            "couldn't",
            "definitely",
            "delay",
            "delaying",
            "didn't",
            "do",
            "doing",
            "don't",
            "ever",
            "even",
            "fine",
            "finally",
            "for",
            "from",
            "get",
            "getting",
            "go",
            "going",
            "good",
            "got",
            "had",
            "have",
            "having",
            "hot",
            "in",
            "into",
            "idea",
            "it",
            "just",
            "long",
            "may",
            "me",
            "might",
            "mistake",
            "must",
            "mustn't",
            "my",
            "myself",
            "necessary",
            "need",
            "needed",
            "needn't",
            "never",
            "normal",
            "not",
            "now",
            "of",
            "off",
            "okay",
            "ok",
            "opposite",
            "other",
            "ought",
            "oughtn't",
            "ourself",
            "ourselves",
            "please",
            "postpone",
            "postponing",
            "probably",
            "proper",
            "quick",
            "really",
            "recommend",
            "recommended",
            "refrain",
            "refraining",
            "remain",
            "remaining",
            "required",
            "safe",
            "sensible",
            "seriously",
            "shall",
            "short",
            "should",
            "shouldn't",
            "skip",
            "skipping",
            "smart",
            "something",
            "stay",
            "staying",
            "still",
            "supposed",
            "take",
            "taking",
            "than",
            "the",
            "time",
            "to",
            "today",
            "tonight",
            "took",
            "us",
            "warm",
            "wise",
            "without",
            "worse",
            "wrong"
        };

        private static readonly HashSet<string> AllowedAfterAction = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "after",
            "again",
            "already",
            "am",
            "and",
            "are",
            "aren't",
            "at",
            "because",
            "before",
            "but",
            "can",
            "can't",
            "correct",
            "could",
            "couldn't",
            "daily",
            "during",
            "eh",
            "every",
            "first",
            "for",
            "if",
            "in",
            "instead",
            "is",
            "isn't",
            "later",
            "less",
            "more",
            "naked",
            "no",
            "normally",
            "now",
            "on",
            "or",
            "please",
            "properly",
            "quickly",
            "regularly",
            "right",
            "so",
            "soon",
            "should",
            "shouldn't",
            "than",
            "then",
            "today",
            "tomorrow",
            "tonight",
            "too",
            "twice",
            "when",
            "while",
            "with",
            "without",
            "would",
            "wouldn't",
            "yes"
        };

        private static readonly Regex WordRegex = new Regex(
            @"[\p{L}\p{N}]+(?:['\u2019][\p{L}\p{N}]+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string? GetResponse(string? question)
        {
            if (question == null || !QuestionValidator.IsQuestion(question))
            {
                return null;
            }

            var normalizedQuestion = question.Normalize(NormalizationForm.FormKC)
                .Replace('\u2019', '\'')
                .ToLowerInvariant();
            var words = GetWords(normalizedQuestion);

            return IsPersonalShowerAdvice(words) || IsImpersonalShowerAdvice(words)
                ? ShowerResponse
                : null;
        }

        private static bool IsPersonalShowerAdvice(IReadOnlyList<string> words)
        {
            for (var subjectIndex = 0; subjectIndex < words.Count; subjectIndex++)
            {
                if (words[subjectIndex] != "i" && words[subjectIndex] != "we")
                {
                    continue;
                }

                var endIndex = Math.Min(words.Count - 1, subjectIndex + MaximumPhraseLength);
                for (var actionIndex = subjectIndex + 1; actionIndex <= endIndex; actionIndex++)
                {
                    if (!BathingActions.Contains(words[actionIndex]) ||
                        !IsSelfBathingUse(words, actionIndex) ||
                        !HasPersonalAdviceFrame(words, subjectIndex, actionIndex))
                    {
                        continue;
                    }

                    if (HasAllowedPathToAction(words, subjectIndex + 1, actionIndex) ||
                        IsReflexiveBathingConstruction(words, actionIndex) ||
                        IsCoordinatedBathingConstruction(words, subjectIndex + 1, actionIndex))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsImpersonalShowerAdvice(IReadOnlyList<string> words)
        {
            for (var cueIndex = 0; cueIndex < words.Count; cueIndex++)
            {
                if (!EvaluationCues.Contains(words[cueIndex]) && words[cueIndex] != "time")
                {
                    continue;
                }

                if (TryFindBoundBathingAction(words, cueIndex + 1, true, out var cueActionIndex) &&
                    IsBathingActionConstruction(words, cueIndex + 1, cueActionIndex))
                {
                    return true;
                }
            }

            if (words.Count < 2 ||
                (words[0] != "is" && words[0] != "are" && words[0] != "was" &&
                 words[0] != "were" && words[0] != "would" && words[0] != "could"))
            {
                return false;
            }

            return TryFindBoundBathingAction(words, 1, false, out var actionIndex) &&
                IsBathingActionConstruction(words, 1, actionIndex) &&
                IsEvaluatedBathingUse(words, actionIndex);
        }

        private static bool HasPersonalAdviceFrame(
            IReadOnlyList<string> words,
            int subjectIndex,
            int actionIndex)
        {
            if (subjectIndex > 0 && DirectAdviceModals.Contains(words[subjectIndex - 1]))
            {
                return true;
            }

            for (var index = subjectIndex + 1; index < actionIndex; index++)
            {
                if (AdviceCues.Contains(words[index]))
                {
                    return true;
                }

                if (index < actionIndex - 1 &&
                    ((words[index] == "have" && words[index + 1] == "to") ||
                     (words[index] == "got" && words[index + 1] == "to")))
                {
                    return true;
                }
            }

            var precedingStartIndex = Math.Max(0, subjectIndex - 6);
            for (var index = precedingStartIndex; index < subjectIndex; index++)
            {
                if (words[index] == "recommend" || words[index] == "recommended")
                {
                    return true;
                }
            }

            if (subjectIndex > 0 && words[subjectIndex - 1] == "if")
            {
                for (var index = precedingStartIndex; index < subjectIndex - 1; index++)
                {
                    if (EvaluationCues.Contains(words[index]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasAllowedPathToAction(
            IReadOnlyList<string> words,
            int startIndex,
            int actionIndex)
        {
            for (var index = startIndex; index < actionIndex; index++)
            {
                if (!AllowedBeforeAction.Contains(words[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsReflexiveBathingConstruction(IReadOnlyList<string> words, int actionIndex)
        {
            if (actionIndex < words.Count - 1 && IsReflexive(words[actionIndex + 1]))
            {
                return true;
            }

            var searchStartIndex = Math.Max(0, actionIndex - 4);
            for (var index = searchStartIndex; index < actionIndex; index++)
            {
                if ((words[index] == "give" || words[index] == "giving") &&
                    index < words.Count - 1 &&
                    IsReflexive(words[index + 1]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsCoordinatedBathingConstruction(
            IReadOnlyList<string> words,
            int startIndex,
            int actionIndex)
        {
            if (!IsExplicitBathingConstruction(words, startIndex, actionIndex))
            {
                return false;
            }

            for (var index = startIndex; index < actionIndex; index++)
            {
                if (BathingCoordinationWords.Contains(words[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsExplicitBathingConstruction(
            IReadOnlyList<string> words,
            int startIndex,
            int actionIndex)
        {
            var searchStartIndex = Math.Max(startIndex, actionIndex - 5);
            for (var index = searchStartIndex; index < actionIndex; index++)
            {
                if (BathingActionIntroducers.Contains(words[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsBathingActionConstruction(
            IReadOnlyList<string> words,
            int startIndex,
            int actionIndex)
        {
            var action = words[actionIndex];
            if (action == "showering" || action == "showered" || action == "unshowered" ||
                action == "bathing" || action == "bathed")
            {
                return true;
            }

            if (IsExplicitBathingConstruction(words, startIndex, actionIndex))
            {
                return true;
            }

            var searchStartIndex = Math.Max(startIndex, actionIndex - 5);
            for (var index = searchStartIndex; index < actionIndex; index++)
            {
                if (words[index] == "to" || words[index] == "avoid" || words[index] == "avoiding" ||
                    words[index] == "skip" || words[index] == "skipping" || words[index] == "without" ||
                    words[index] == "stay" || words[index] == "staying" || words[index] == "remain" ||
                    words[index] == "remaining")
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindBoundBathingAction(
            IReadOnlyList<string> words,
            int startIndex,
            bool validateFollowingWord,
            out int actionIndex)
        {
            var endIndex = Math.Min(words.Count - 1, startIndex + MaximumPhraseLength);
            for (var index = startIndex; index <= endIndex; index++)
            {
                if (BathingActions.Contains(words[index]))
                {
                    if (!validateFollowingWord || IsSelfBathingUse(words, index))
                    {
                        actionIndex = index;
                        return true;
                    }

                    actionIndex = -1;
                    return false;
                }

                if (!AllowedBeforeAction.Contains(words[index]))
                {
                    actionIndex = -1;
                    return false;
                }
            }

            actionIndex = -1;
            return false;
        }

        private static bool IsSelfBathingUse(IReadOnlyList<string> words, int actionIndex)
        {
            if (actionIndex == words.Count - 1 || IsReflexive(words[actionIndex + 1]))
            {
                return true;
            }

            return AllowedAfterAction.Contains(words[actionIndex + 1]);
        }

        private static bool HasEvaluationCueAfter(IReadOnlyList<string> words, int actionIndex)
        {
            var endIndex = Math.Min(words.Count - 1, actionIndex + 8);
            for (var index = actionIndex + 1; index <= endIndex; index++)
            {
                if (EvaluationCues.Contains(words[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEvaluatedBathingUse(IReadOnlyList<string> words, int actionIndex)
        {
            if (actionIndex >= words.Count - 1 || !HasEvaluationCueAfter(words, actionIndex))
            {
                return false;
            }

            var followingWord = words[actionIndex + 1];
            return EvaluationCues.Contains(followingWord) ||
                AllowedAfterAction.Contains(followingWord) ||
                followingWord == "a" ||
                followingWord == "an" ||
                followingWord == "be" ||
                followingWord == "being" ||
                followingWord == "the";
        }

        private static bool IsReflexive(string word)
        {
            return word == "myself" || word == "ourself" || word == "ourselves";
        }

        private static List<string> GetWords(string text)
        {
            var matches = WordRegex.Matches(text);
            var words = new List<string>(matches.Count);

            foreach (Match match in matches)
            {
                words.Add(match.Value);
            }

            return words;
        }
    }
}
