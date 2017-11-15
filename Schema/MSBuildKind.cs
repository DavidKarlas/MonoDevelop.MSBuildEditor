﻿// Copyright (c) 2014 Xamarin Inc.
// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;

namespace MonoDevelop.MSBuildEditor.Schema
{
	enum MSBuildKind
	{
		Choose,
		Import,
		ImportGroup,
		Item,
		ItemDefinitionGroup,
		ItemDefinition,
		ItemGroup,
		Metadata,
		OnError,
		Otherwise,
		Output,
		Parameter,
		ParameterGroup,
		Project,
		ProjectExtensions,
		Property,
		PropertyGroup,
		Target,
		Task,
		TaskBody,
		UsingTask,
		When,
		ItemReference,
		PropertyReference,
		MetadataReference,
		TaskParameter,
	}
}
