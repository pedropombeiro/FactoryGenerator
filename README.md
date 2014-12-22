# FactoryGenerator #

[![Build Status](https://travis-ci.org/minidfx/FactoryGenerator.svg)](https://travis-ci.org/minidfx/FactoryGenerator)
[![NuGet](https://img.shields.io/nuget/dt/DeveloperInTheFlow.FactoryGenerator.svg)](http://www.nuget.org/packages/DeveloperInTheFlow.FactoryGenerator/)


Tools for generating factories of your classes marked by the attribute GenerateFactory. It will generate a file (&lt;TargetClass&gt;Factory.Generated.cs) at the same location than the class file.

Add the attribute **GenerateFactory** on your class

    [GenerateFactory(typeof(IFooFactory))]
    public class Foo {
      // ...
    }

The tool will generate the file **FooFactory.Generated.cs** as the same location than the class **Foo**.

You can generate factories by executing **GenerateFactories.bat** with the solution as argument

    GenerateFactories.bat -s <your solution path>

or through Visual Studio as an external tool, follow steps for using it in Visual Studio.

* Open the external tool dialog by clicking on the menu **Tools -> External Tools ...**.
* Add a new external tool by clicking on the button **Add**.
* And then fill fields with these values:
 * Title: **Generate Factories**
 * Command: **$(SolutionDir)\GenerateFactories.bat**
 * Arguments: **-s "$(SolutionDir)\$(SolutionFileName)"**
 * Initial directory: **$(SolutionDir)**
* Check the checkbox **Use Output window**.
* Click on the button **OK**.
* Run it in Visual Studio by clicking on the menu **Tools -> Generate Factories**.

Enjoy!
