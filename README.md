# FactoryGenerator #

[![Build Status](https://travis-ci.org/minidfx/FactoryGenerator.svg)](https://travis-ci.org/minidfx/FactoryGenerator)

Tools for generating factories of classes with Roselyn marked by the attribute GenerateFactory. It will generate a file at the same location than the class file with a suffix *.generated.cs*.

To use the FactoryGenerator, you have to configure an external tool in Visual Studio.

* Open the external tool dialog by clicking on : **Tools -> External Toos ...**
* Add a new external tool by clicking on **Add** button.
* And then fill fields with these values:
 * Title: **Generate Factories**
 * Command: **$(SolutionDir)\GenerateFactories.bat**
 * Arguments: **-s "$(SolutionDir)\$(SolutionFileName)"**
 * Initial directory: **$(SolutionDir)**
 * Check **Use Output window**
* Run it by clicking on: **Tools -> Generate Factories**.

Enjoy!
