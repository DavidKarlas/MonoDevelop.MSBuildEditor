Things that still need to be ported from the MonoDevelop version:

MSBuild:

* switch between related files command

Xml:

* improve the background parser scheduling
* formatter
* outlining tagger that's better than the one from textmate
* document outline tool window
* breadcrumbs bar

Although there are hundred of unit tests covering the lower level details, there are few/no tests in the following areas:

* validation
* schema based completion
* completion for various value types
* function completion & resolution
* schema inference

Longer term:

* switch the XDom to use a red-green tree like Roslyn
* implement some incremental parsing. it should be fairly straightforward to do it for a subset of insertions, especially without < chars
