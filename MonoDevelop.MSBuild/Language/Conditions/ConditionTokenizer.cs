//
// ConditionTokenizer.cs
//
// Author:
//   Marek Sieradzki (marek.sieradzki@gmail.com)
//   Jaroslaw Kowalski <jaak@jkowalski.net>
// 
// (C) 2006 Marek Sieradzki
// (C) 2004-2006 Jaroslaw Kowalski
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text;

namespace MonoDevelop.MSBuild.Language.Conditions
{

	internal sealed class ConditionTokenizer
	{

		string inputString = null;
		int position = 0;
		int tokenPosition = 0;
		int tokenLength = 0;
		int nextChar = -1;

		Token token;
		bool hasPutback;
		Token putback;

		//		bool	ignoreWhiteSpace = true;

		static TokenType [] charIndexToTokenType = new TokenType [128];
		static Dictionary<string, TokenType> keywords = new Dictionary<string, TokenType> (StringComparer.OrdinalIgnoreCase);

		static ConditionTokenizer ()
		{
			for (int i = 0; i < 128; i++)
				charIndexToTokenType [i] = TokenType.Invalid;

			foreach (CharToTokenType cht in charToTokenType)
				charIndexToTokenType [(int)cht.ch] = cht.tokenType;

			keywords.Add ("and", TokenType.And);
			keywords.Add ("or", TokenType.Or);
		}

		public ConditionTokenizer ()
		{
			//			this.ignoreWhiteSpace = true;
		}

		public void Tokenize (string s)
		{
			if (s == null)
				throw new ArgumentNullException ("s");

			this.inputString = s;
			this.position = 0;
			if (position < inputString.Length)
				this.nextChar = inputString [position];
			this.token = new Token (null, TokenType.BOF, 0);

			GetNextToken ();
		}

		void SkipWhiteSpace ()
		{
			int ch;

			while ((ch = PeekChar ()) != -1) {
				if (!Char.IsWhiteSpace ((char)ch))
					break;
				ReadChar ();
			}
		}

		int PeekChar ()
		{
			return nextChar;
		}

		[MethodImpl (MethodImplOptions.AggressiveInlining)]
		void ReadChar ()
		{
			if (++position < inputString.Length)
				nextChar = inputString [position];
			else
				nextChar = -1;
		}

		public void Expect (TokenType type)
		{
			if (token.Type != type)
				throw new ExpressionParseException ("Expected token type of type: " + type + ", got " + token.Type +
					" (" + token.Value + ") .");

			GetNextToken ();
		}

		public bool IsEOF ()
		{
			return token.Type == TokenType.EOF;
		}

		public bool IsNumber ()
		{
			return token.Type == TokenType.Number;
		}

		public bool IsToken (TokenType type)
		{
			return token.Type == type;
		}

		public bool IsPunctation ()
		{
			return (token.Type >= TokenType.FirstPunct && token.Type < TokenType.LastPunct);
		}

		// FIXME test this
		public void Putback (Token token)
		{
			hasPutback = true;
			putback = token;
		}

		public void GetNextToken ()
		{
			if (hasPutback) {
				token = putback;
				hasPutback = false;
				return;
			}

			if (token.Type == TokenType.EOF)
				throw new ExpressionParseException (String.Format (
							"Error while parsing condition \"{0}\", ended abruptly.",
							inputString));

			SkipWhiteSpace ();

			tokenPosition = position;
			tokenLength = 0;

			int i = PeekChar ();
			ReadChar ();

			if (i == -1) {
				token = new Token (null, TokenType.EOF, tokenPosition);
				return;
			}

			char ch = (char)i;

			// FIXME: looks like a hack: if '-' is here '->' won't be tokenized
			// maybe we should treat item reference as a token
			if (ch == '-' && PeekChar () == '>') {
				ReadChar ();
				token = new Token ("->", TokenType.Transform, tokenPosition);
			} else if (Char.IsDigit (ch) || ch == '-') {

				tokenLength++;

				while ((i = PeekChar ()) != -1) {
					ch = (char)i;

					if (Char.IsDigit (ch) || ch == '.') {
						ReadChar ();
						tokenLength++;
					} else
						break;
				}

				token = new Token (inputString.Substring (tokenPosition, tokenLength), TokenType.Number, tokenPosition);
			} else if (ch == '\'' && position < inputString.Length) {

				tokenLength++;

				bool is_itemref = (PeekChar () == '@');
				int num_open_braces = 0;
				bool in_literal = false;

				while ((i = PeekChar ()) != -1) {
					ch = (char)i;
					if (ch == '(' && !in_literal && is_itemref)
						num_open_braces++;
					if (ch == ')' && !in_literal && is_itemref)
						num_open_braces--;

					ReadChar ();
					tokenLength++;

					if (ch == '\'') {
						if (num_open_braces == 0)
							break;
						in_literal = !in_literal;
					}
				}

				token = new Token (inputString.Substring (tokenPosition + 1, tokenLength - 2), TokenType.String, tokenPosition);

			} else if (ch == '_' || Char.IsLetter (ch)) {
				tokenLength++;

				while ((i = PeekChar ()) != -1) {
					if ((char)i == '_' || Char.IsLetterOrDigit ((char)i)) {
						ReadChar ();
						tokenLength++;
					} else
						break;
				}

				string temp = inputString.Substring (tokenPosition, tokenLength);

				if (!keywords.TryGetValue (temp, out TokenType tokenType))
					tokenType = TokenType.String;
				token = new Token (temp, tokenType, tokenPosition);

			} else if (ch == '!' && PeekChar () == (int)'=') {
				token = new Token ("!=", TokenType.NotEqual, tokenPosition);
				ReadChar ();
			} else if (ch == '<' && PeekChar () == (int)'=') {
				token = new Token ("<=", TokenType.LessOrEqual, tokenPosition);
				ReadChar ();
			} else if (ch == '>' && PeekChar () == (int)'=') {
				token = new Token (">=", TokenType.GreaterOrEqual, tokenPosition);
				ReadChar ();
			} else if (ch == '=' && PeekChar () == (int)'=') {
				token = new Token ("==", TokenType.Equal, tokenPosition);
				ReadChar ();
			} else if (ch >= 32 && ch < 128) {
				if (charIndexToTokenType [ch] != TokenType.Invalid) {
					token = new Token (new String (ch, 1), charIndexToTokenType [ch], tokenPosition);
					return;
				} else
					throw new ExpressionParseException (String.Format ("Invalid punctuation: {0}", ch));
			} else
				throw new ExpressionParseException (String.Format ("Invalid token: {0}", ch));
		}

		public void ScanForClosingParens (int parensCounter = 1)
		{
			tokenPosition = position;
			int start = position;
			int ch;
			while ((ch = PeekChar ()) >= 0) {
				switch (ch) {
				case ')':
					if (--parensCounter == 0) {
						token = new Token (inputString.Substring (start, position - start), TokenType.String, tokenPosition);
						return;
					}
					break;
				case '(':
					++parensCounter;
					break;
				}
				ReadChar ();
			}

			token = new Token (null, TokenType.EOF, tokenPosition);
		}

		public int TokenPosition {
			get { return tokenPosition; }
		}

		public Token Token {
			get { return token; }
		}

		/*
				public bool IgnoreWhiteSpace {
					get { return ignoreWhiteSpace; }
					set { ignoreWhiteSpace = value; }
				}
		*/

		struct CharToTokenType
		{
			public char ch;
			public TokenType tokenType;

			public CharToTokenType (char ch, TokenType tokenType)
			{
				this.ch = ch;
				this.tokenType = tokenType;
			}
		}

		static CharToTokenType [] charToTokenType = {
			new CharToTokenType ('<', TokenType.Less),
			new CharToTokenType ('>', TokenType.Greater),
			new CharToTokenType ('=', TokenType.Equal),
			new CharToTokenType ('(', TokenType.LeftParen),
			new CharToTokenType (')', TokenType.RightParen),
			new CharToTokenType ('.', TokenType.Dot),
			new CharToTokenType (',', TokenType.Comma),
			new CharToTokenType ('!', TokenType.Not),
			new CharToTokenType ('@', TokenType.Item),
			new CharToTokenType ('$', TokenType.Property),
			new CharToTokenType ('%', TokenType.Metadata),
			new CharToTokenType ('\'', TokenType.Apostrophe),
		};
	}
}