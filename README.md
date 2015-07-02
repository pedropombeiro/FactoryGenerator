FactoryGenerator
================

[![Build Status](https://img.shields.io/travis/minidfx/FactoryGenerator.svg)](https://travis-ci.org/minidfx/FactoryGenerator) [![NuGet](https://img.shields.io/nuget/dt/DeveloperInTheFlow.FactoryGenerator.svg)](http://www.nuget.org/packages/DeveloperInTheFlow.FactoryGenerator/)

Tools for generating factories of your classes marked by the attribute GenerateFactory. It will generate a file (&lt;TargetClass&gt;Factory.Generated.cs) at the same location than the class file.

Add the attribute **GenerateFactory** on your class

```
[GenerateFactory(typeof(IFooFactory))]
public class Foo {
  // ...
}
```

The tool will generate the file **FooFactory.Generated.cs** as the same location than the class **Foo**.

You can generate factories by executing **GenerateFactories.bat** with the solution as argument

```
GenerateFactories.bat -s <your solution path> [-t <templatePath>] [-d] [-a <attribute1, attributeN>]
```

or through Visual Studio as an external tool, follow steps for using it in Visual Studio.

-	Open the external tool dialog by clicking on the menu **Tools -> External Tools ...**.
-	Add a new external tool by clicking on the button **Add**.
-	And then fill fields with these values:
	-	Title: **Generate Factories**
	-	Command: **$(SolutionDir)\GenerateFactories.bat**
	-	Solution: **-s "$(SolutionDir)\$(SolutionFileName)"**
	-	Template (optional): **-t <templatePath>** (by default the DefaultTemplate.render file is used for rendering the factory)
	-	Comments: **-d** (Copy the documentation from the interface factory into the implementation)
	-	Attributes import list: **-a** (Attributes that will be injected on the factory constructor)
	-	Initial directory: **$(SolutionDir)**
-	Check the checkbox **Use Output window**.
-	Click on the button **OK**.
-	Run it in Visual Studio by clicking on the menu **Tools -> Generate Factories**.

Enjoy!

Advanced features
-----------------

### Custom template in according an interface

You can add a custom template naming with the same name than the interface factory to customize the output of some factories.

If you have your main template **&lt;path&gt;\Templates\DefaultTemplate.render**, the factory generator will search whether a template in the same folder with the factory interface name (i.e: &lt;interfaceFactoryName&gt;.render) exists and use it instead of the main template.

### Model transformation

You can tranform the model just before the template rendering by creating a C# Script with the same name than the interface factory to customize the model passed to [Nustache](https://github.com/jdiamond/Nustache) for the rendering.

If you have your main template **&lt;path&gt;\Templates\DefaultTemplate.render**, the factory generator will search whether a C# script exists in the same folder with the factory interface name (i.e: &lt;interfaceFactoryName&gt;.tcs) exists and execute the method **Transform** from the C# Script.

Below the method signature that the script has to define to be used.

```
using ...

using Newtonsoft.Json;        // Mandatory
using Newtonsoft.Json.Linq;   // Mandatory

public class Script
{
  public JObject Transform(JObject json)
  {
      // Your transformation ...
  }
}
```

As you can see, the script use [JSON.Net](https://github.com/JamesNK/Newtonsoft.Json) library to make its transformations.
