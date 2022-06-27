# OpenAPI Linker

An Azure Function for inlining external JSON Schema references in OpenAPI JSON specs.

> 👷🏻‍♂️⛔👷🏻‍♀️ Work in progress!

## Getting started

You will need:

* dotnet 6 sdk / Visual Studio 2022
* Azure Functions SDK

At the terminal:

    cd src/OpenApiLinker
    func start

Now you will have a running function at http://localhost:7071 (or some port)

    GET http://localhost:7071/api/linker?openApiUrl=(url encoded URL to OpenAPI 3 spec)

or in `curl`

    curl http://localhost:7071/api/linker?openApiUrl=(url encoded URL to OpenAPI 3 spec)

