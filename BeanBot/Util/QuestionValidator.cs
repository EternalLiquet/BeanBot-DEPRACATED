#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BeanBot.Util
{
    public static class QuestionValidator
    {
        private static readonly HashSet<string> InterrogativeWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "how",
            "what",
            "when",
            "where",
            "which",
            "who",
            "whom",
            "whose",
            "why"
        };

        private static readonly HashSet<string> AuxiliaryWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ain't",
            "am",
            "are",
            "aren't",
            "can",
            "can't",
            "cannot",
            "could",
            "couldn't",
            "did",
            "didn't",
            "do",
            "does",
            "doesn't",
            "don't",
            "had",
            "hadn't",
            "has",
            "hasn't",
            "have",
            "haven't",
            "is",
            "isn't",
            "may",
            "might",
            "mightn't",
            "must",
            "mustn't",
            "needn't",
            "ought",
            "oughtn't",
            "shall",
            "shan't",
            "should",
            "shouldn't",
            "was",
            "wasn't",
            "were",
            "weren't",
            "will",
            "won't",
            "would",
            "wouldn't"
        };

        private static readonly HashSet<string> SubjectWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "anybody",
            "anyone",
            "everybody",
            "everyone",
            "he",
            "i",
            "it",
            "nobody",
            "nothing",
            "she",
            "somebody",
            "someone",
            "something",
            "that",
            "there",
            "these",
            "they",
            "this",
            "those",
            "we",
            "who",
            "you"
        };

        private static readonly HashSet<string> FiniteAuxiliaryWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "am",
            "are",
            "can",
            "could",
            "did",
            "does",
            "had",
            "has",
            "is",
            "may",
            "might",
            "must",
            "shall",
            "should",
            "was",
            "were",
            "will",
            "would"
        };

        private static readonly HashSet<string> NonFiniteComplementOpeners = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "am",
            "are",
            "aren't",
            "can",
            "can't",
            "could",
            "couldn't",
            "did",
            "didn't",
            "do",
            "does",
            "doesn't",
            "don't",
            "is",
            "isn't",
            "may",
            "might",
            "mightn't",
            "must",
            "mustn't",
            "shall",
            "shan't",
            "should",
            "shouldn't",
            "was",
            "wasn't",
            "were",
            "weren't",
            "will",
            "won't",
            "would",
            "wouldn't"
        };

        private static readonly HashSet<string> ConfirmationTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "correct",
            "eh",
            "no",
            "right",
            "yes"
        };

        private static readonly HashSet<string> InterrogativeContractionSuffixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "d",
            "ll",
            "re",
            "s",
            "ve"
        };

        private static readonly Regex DiscordObjectRegex = new Regex(
            @"<(?:(?:@!?|@&|#)\d+|a?:[^:>\s]+:\d+)>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex UrlRegex = new Regex(
            @"\b(?:https?://|www\.)[^\s,;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex WordRegex = new Regex(
            @"[\p{L}\p{N}]+(?:['\u2019][\p{L}\p{N}]+)*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly char[] ClosingCharacters =
        {
            '\"', '\'', '\u2019', '\u201D', ')', ']', '}', '>', '*', '_', '~', '`', '|'
        };

        private static readonly char[] ClauseSeparators = { ',', ';', ':' };

        public static bool IsQuestion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalizedText = text.Trim().TrimEnd(ClosingCharacters);
            if (!TryGetQuestionBody(normalizedText, out var questionBody))
            {
                return false;
            }

            questionBody = DiscordObjectRegex.Replace(questionBody, " item ");
            questionBody = UrlRegex.Replace(questionBody, " item ");

            if (IsInterrogativeClause(questionBody))
            {
                return true;
            }

            var lastSeparatorIndex = questionBody.LastIndexOfAny(ClauseSeparators);
            if (lastSeparatorIndex < 0)
            {
                return false;
            }

            var leadingClause = questionBody.Substring(0, lastSeparatorIndex);
            var finalClause = questionBody.Substring(lastSeparatorIndex + 1);

            return IsInterrogativeClause(finalClause) || IsConfirmationTag(leadingClause, finalClause);
        }

        private static bool TryGetQuestionBody(string text, out string questionBody)
        {
            var punctuationIndex = text.Length - 1;
            var hasQuestionMark = false;

            while (punctuationIndex >= 0 && (text[punctuationIndex] == '?' || text[punctuationIndex] == '!'))
            {
                hasQuestionMark |= text[punctuationIndex] == '?';
                punctuationIndex--;
            }

            questionBody = text.Substring(0, punctuationIndex + 1).Trim();
            return hasQuestionMark && questionBody.Length > 0;
        }

        private static bool IsInterrogativeClause(string clause)
        {
            var words = WordRegex.Matches(clause);
            if (words.Count == 0)
            {
                return false;
            }

            var normalizedFirstWord = NormalizeWord(words[0].Value);
            var firstWord = NormalizeQuestionOpener(normalizedFirstWord);
            if (InterrogativeWords.Contains(firstWord))
            {
                if (words.Count == 1)
                {
                    // A question word by itself (for example, "why?") does not
                    // provide enough of a proposition for the fortune command.
                    return false;
                }

                var secondWord = NormalizeWord(words[1].Value);
                var isContractedInterrogative = !firstWord.Equals(normalizedFirstWord, StringComparison.OrdinalIgnoreCase);
                if (!isContractedInterrogative && SubjectWords.Contains(secondWord))
                {
                    return false;
                }

                return !firstWord.Equals("what", StringComparison.OrdinalIgnoreCase) ||
                    (!secondWord.Equals("a", StringComparison.OrdinalIgnoreCase) &&
                     !secondWord.Equals("an", StringComparison.OrdinalIgnoreCase));
            }

            if (!AuxiliaryWords.Contains(firstWord) || words.Count < 2)
            {
                return false;
            }

            var secondAuxiliaryWord = NormalizeWord(words[1].Value);
            if (words.Count == 2)
            {
                return SubjectWords.Contains(secondAuxiliaryWord);
            }

            var thirdWord = NormalizeWord(words[2].Value);
            return !FiniteAuxiliaryWords.Contains(secondAuxiliaryWord) &&
                (!NonFiniteComplementOpeners.Contains(firstWord) || !FiniteAuxiliaryWords.Contains(thirdWord));
        }

        private static bool IsConfirmationTag(string leadingClause, string finalClause)
        {
            var leadingWords = WordRegex.Matches(leadingClause);
            var finalWords = WordRegex.Matches(finalClause);

            return leadingWords.Count >= 2 &&
                finalWords.Count == 1 &&
                ConfirmationTags.Contains(NormalizeWord(finalWords[0].Value));
        }

        private static string NormalizeWord(string word)
        {
            return word.Replace('\u2019', '\'');
        }

        private static string NormalizeQuestionOpener(string word)
        {
            var normalizedWord = NormalizeWord(word);
            var contractionIndex = normalizedWord.IndexOf('\'');
            if (contractionIndex <= 0)
            {
                return normalizedWord;
            }

            var baseWord = normalizedWord.Substring(0, contractionIndex);
            var suffix = normalizedWord.Substring(contractionIndex + 1);
            return InterrogativeWords.Contains(baseWord) && InterrogativeContractionSuffixes.Contains(suffix)
                ? baseWord
                : normalizedWord;
        }
    }
}
