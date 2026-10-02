# Mi propuesta — Problema 1

Primero quiero aclarar una cuestión conceptual:

**¿`TradeBarRow` y `QuoteBarRow` son tipos del lado de LEAN/C# que actúan como adaptadores para crear los objetos de LEAN, representan la estructura física esperada de los archivos binarios, o actualmente cumplen ambas funciones al mismo tiempo?**

Dependiendo de esa respuesta, mi propuesta es dejar de asumir que un archivo `.bin` tiene un formato determinado únicamente por el tipo de barra que LEAN espera recibir.

En cambio, cada archivo físico debería poder corresponder a cualquiera de los formatos binarios soportados, y el sistema debería identificar y validar cuál de ellos contiene realmente.

Podríamos conservar los nombres `TradeBarRow` y `QuoteBarRow`, mejorarlos o sustituirlos por una abstracción más clara. Una posibilidad sería introducir el concepto de:

```text
PhysicalRecordType
```

o `PRT` como abreviación durante la discusión.

La idea sería que `PhysicalRecordType` represente exclusivamente **cómo está estructurado físicamente un registro dentro de un archivo `.bin`**, independientemente del tipo lógico que posteriormente solicite LEAN.

En el contexto actual de Auroboros, por ejemplo, nuestros históricos Forex tienen físicamente:

```text
Timestamp
+
OHLCV
+
Bid OHLC
+
Ask OHLC
```

Por tanto, dentro de esta clasificación física, ese archivo correspondería a un tipo equivalente a `QuoteBarRow`.

En cambio, un archivo que contuviera únicamente:

```text
Timestamp
+
OHLCV
```

correspondería a un tipo equivalente a `TradeBarRow`.

Mi propuesta sería desacoplar estos formatos físicos mediante una abstracción más general, de manera que podamos identificar primero el `PhysicalRecordType` del archivo y, posteriormente, utilizarlo para construir el tipo de dato que LEAN necesite.

La ubicación concreta de esta abstracción —por ejemplo, dentro de `Interfaces` de nuestra rama de LEAN o en otra parte más apropiada— debería decidirse después de revisar cómo LEAN vanilla estructura problemas equivalentes. Si podemos seguir una convención nativa de LEAN, mejor. Si no existe una abstracción adecuada para nuestro caso, prefiero crear archivos propios para nuestra customización antes que modificar innecesariamente componentes nativos.

Antes de tomar esta decisión arquitectónica, quiero que revisemos el estado actualizado de LEAN. Si es necesario, primero pondré `master` de mi fork al día con `upstream`, que es el nombre que le di al remote oficial, y después volveré a colocar mi rama `auroboros` por encima de ese `master`. La idea es que las decisiones se basen siempre en la versión más reciente de LEAN vanilla y que nuestro patch permanezca por delante de ella.

---

## Estandarización de los formatos físicos

Actualmente hablamos de conceptos como `FxQuote64`, `TradeBar32`, `QuoteBarRow`, etc.

Mi intención es estandarizar estos nombres y responsabilidades para que las mejoras binarias de Auroboros puedan mantenerse en el tiempo.

La idea de `PhysicalRecordType` busca precisamente eso: que exista una definición formal de los distintos formatos físicos que puede contener un `.bin` y que la documentación establezca claramente cómo está compuesto cada uno.

Por ejemplo, podríamos terminar conceptualmente con algo como:

```text
PhysicalRecordType
    ├── TradeBarRow
    └── QuoteBarRow
```

Los nombres definitivos todavía pueden cambiar, pero la responsabilidad debería ser clara: describir el formato físico de los registros, no el tipo lógico solicitado por LEAN.

---

## Caso general: mercados donde LEAN solo necesita TradeBar

Pensemos en un ejemplo hipotético con acciones.

Podríamos tener una estructura de datos como:

```text
Data/historical_data/
    <SIGLAS_TIPO_MERCADO>_<ACTIVO>_data/
        <SIGLAS_TIPO_MERCADO>_<ACTIVO>_<FECHA_INICIO>-<FECHA_FIN>_bin_data/
```

Y dentro, por ejemplo:

```text
1-EQ_S&P500_5M_20091026-20100417.bin
2-EQ_S&P500_5M_20100418-20101008.bin
3-EQ_S&P500_5M_20101009-20110331.bin
4-EQ_S&P500_5M_20110401-20110921.bin
5-EQ_S&P500_5M_20110922-20120313.bin
6-EQ_S&P500_5M_20120314-20120903.bin
```

Supongamos que para ese mercado LEAN únicamente necesita construir `TradeBar`.

Podrían existir tres escenarios físicos:

### Escenario 1

Los seis archivos utilizan un `PhysicalRecordType` equivalente a `QuoteBarRow`.

En ese caso, el sistema valida cada archivo como `QuoteBarRow` y utiliza únicamente los campos necesarios para proyectar el `TradeBar` que LEAN espera.

### Escenario 2

Los seis archivos utilizan un `PhysicalRecordType` equivalente a `TradeBarRow`.

En ese caso, se validan como `TradeBarRow` y se construye directamente el `TradeBar` utilizando su OHLCV.

### Escenario 3

Dentro de la misma carpeta existen ambos tipos físicos mezclados.

Por ejemplo:

```text
QuoteBarRow
TradeBarRow
QuoteBarRow
QuoteBarRow
TradeBarRow
TradeBarRow
```

No debería importar si la distribución es:

```text
50 / 50
60 / 40
90 / 10
```

ni cuál aparece primero.

Cada archivo debería validar individualmente su `PhysicalRecordType`.

Si ambos formatos contienen información suficiente para producir el `TradeBar` esperado por ese mercado, ambos deberían poder participar del mismo stream lógico.

En otras palabras:

```text
archivo 1 → detectar PRT → proyectar TradeBar
archivo 2 → detectar PRT → proyectar TradeBar
archivo 3 → detectar PRT → proyectar TradeBar
...
```

El tipo físico de un archivo no debería obligar a que todos los demás archivos de la carpeta tengan exactamente el mismo formato si el tipo lógico requerido por LEAN puede obtenerse correctamente desde cualquiera de los formatos permitidos.

---

## Caso Forex

Para Forex propongo una política más estricta.

Si determinamos que LEAN necesita `QuoteBar` para este mercado, desde el comienzo debería validarse que todos los archivos físicos utilizados para ese activo sean del `PhysicalRecordType` correspondiente a `QuoteBarRow`.

Conceptualmente:

```text
Forex
    ↓
LEAN necesita QuoteBar
    ↓
todos los archivos deben contener QuoteBarRow
```

Si uno o varios archivos no cumplen ese contrato, el dataset debería considerarse inválido para esa request Forex y la ejecución debería fallar.

Sin embargo, la validación no debería detenerse al encontrar el primer archivo incompatible. Antes de abortar la ejecución, debería completar la comprobación de todos los archivos candidatos y recopilar la lista completa de aquellos que incumplen el contrato.

Por ejemplo:

```text
archivo 1 → QuoteBarRow → válido
archivo 2 → TradeBarRow → inválido
archivo 3 → QuoteBarRow → válido
archivo 4 → TradeBarRow → inválido
archivo 5 → TradeBarRow → inválido
```

El resultado debería ser un único fallo de validación que informe conjuntamente que los archivos 2, 4 y 5 son incompatibles.

La razón es que `TradeBarRow` no contendría la información Bid/Ask necesaria para construir correctamente un `QuoteBar`.

De esta manera, el usuario puede conocer en una sola ejecución el estado completo del dataset y no descubrir archivos incompatibles de uno en uno mediante ejecuciones sucesivas.

---

## Validación según mercado y tipo esperado por LEAN

En términos generales, mi propuesta sería:

```text
identificar mercado
    ↓
determinar qué tipo lógico necesita LEAN
    ↓
detectar el PhysicalRecordType de cada archivo
    ↓
validar si cada PRT puede producir el tipo lógico requerido
    ↓
recopilar todos los archivos incompatibles
    ↓
si existen errores → informar todos y abortar
si no existen errores → leer/proyectar
```

Si el mercado necesita `QuoteBar`:

```text
todos los archivos
→ deben tener un PRT capaz de producir QuoteBar
```

Si actualmente eso significa exclusivamente `QuoteBarRow`, la existencia de uno o varios archivos incompatibles debería invalidar la ejecución.

La comprobación, sin embargo, debería realizarse sobre el conjunto completo de archivos antes de reportar el error, para entregar al usuario todos los problemas detectados en una única validación.

Si el mercado necesita `TradeBar`:

```text
TradeBarRow → válido
QuoteBarRow → válido si puede proyectarse a TradeBar
```

y deberían permitirse los tres escenarios descritos anteriormente: todos `TradeBarRow`, todos `QuoteBarRow` o cualquier combinación de ambos.

Si uno o varios archivos no corresponden a ninguno de los `PhysicalRecordType` admitidos para producir el tipo lógico requerido, todos ellos deberían recopilarse e informarse conjuntamente antes de abortar.

Por supuesto, si en la revisión de LEAN encontramos otros mercados o tipos de datos que requieran reglas diferentes, habría que incorporarlos explícitamente al contrato.

---

## Escalabilidad de la validación previa

Esta fase debe diseñarse considerando que un dataset puede contener desde unos pocos archivos hasta decenas, centenas o miles de `.bin`.

La validación individual de cada archivo es independiente, por lo que no considero necesario recorrer todo el conjunto estrictamente de forma secuencial. Sin embargo, tampoco considero apropiado crear un thread, `Task` o worker independiente por cada archivo.

La propuesta es utilizar un modelo de **lotes balanceados con paralelismo acotado**.

Conceptualmente:

```text
N archivos candidatos
        ↓
determinar grado de paralelismo apropiado
según recursos disponibles
        ↓
crear K lotes balanceados
        ↓
K workers concurrentes
        ↓
cada worker procesa secuencialmente
los archivos de su lote
        ↓
agregar todos los resultados
```

De esta forma, el número de archivos no determina directamente el número de hilos utilizados.

Por ejemplo, disponer de 2.000 archivos no implicaría crear 2.000 threads.

El sistema determinaría primero un **grado máximo de paralelismo** razonable para el entorno y utilizaría como máximo esa cantidad de workers concurrentes.

Conceptualmente:

```text
K = min(
    cantidad de archivos,
    grado de paralelismo permitido
)
```

y después distribuiría los archivos entre esos `K` workers.

Cada worker recibiría un lote:

```text
Worker 1
    archivo 1
    archivo 5
    archivo 9
    ...

Worker 2
    archivo 2
    archivo 6
    archivo 10
    ...

Worker 3
    archivo 3
    archivo 7
    archivo 11
    ...
```

El ejemplo únicamente ilustra la distribución; el algoritmo concreto debería intentar que los lotes tengan un coste de validación aproximadamente equivalente.

Si identificar un `PhysicalRecordType` termina siendo una operación prácticamente constante por archivo —por ejemplo, mediante metadata, header o una inspección mínima— bastaría con balancear los lotes principalmente por cantidad de archivos.

Si la validación requiere inspeccionar una cantidad significativa de datos y el coste depende del tamaño del `.bin`, podría ser preferible balancearlos también considerando su tamaño o algún otro estimador barato del trabajo requerido.

La implementación concreta debería aprovechar las primitivas modernas de concurrencia de .NET y las convenciones existentes en LEAN, en lugar de administrar manualmente threads cuando no sea necesario.

Por tanto, en este punto no quiero imponer todavía una clase concreta como:

```text
Thread
Task
Parallel.ForEach
Parallel.ForEachAsync
```

La revisión del LEAN actualizado deberá determinar qué mecanismo encaja mejor.

Lo que sí quiero fijar como intención arquitectónica es:

> La validación física del dataset debe poder repartirse en un número acotado de workers y lotes balanceados, dimensionados según los recursos disponibles y el coste real de la operación, evitando tanto el procesamiento completamente serial innecesario como la creación descontrolada de concurrencia por archivo.

El patrón toma como referencia una estrategia que ya utilizamos en `bot-fix`: dividir `N` elementos en batches y procesarlos concurrentemente.

Sin embargo, para este caso quiero una versión más conservadora y escalable.

En `bot-fix`, las operaciones pertenecientes a un mismo batch podían ejecutarse mediante múltiples threads. Para archivos binarios potencialmente numerosos, prefiero inicialmente el siguiente modelo:

```text
1 worker
    ↓
1 lote
    ↓
archivos del lote procesados secuencialmente
```

con varios workers ejecutando distintos lotes en paralelo.

Esto limita de forma natural:

```text
threads/tasks activas
handles de archivos
MemoryMappedFiles o streams temporales
presión sobre el scheduler
presión sobre disco
```

sin renunciar al paralelismo.

La cantidad de workers no debería derivarse únicamente del número de cores de CPU de forma rígida.

La operación combina potencialmente:

```text
filesystem I/O
+
lectura de metadata/bytes
+
validación CPU
```

por lo que la política definitiva debería partir de un valor razonable relacionado con los recursos del entorno y confirmarse mediante medición.

No quiero fijar desde esta propuesta una fórmula arbitraria antes de conocer exactamente cuánto trabajo exige la detección final del `PhysicalRecordType`.

Sí quiero que la arquitectura permita ajustar ese **degree of parallelism** sin cambiar la semántica del sistema.

También es importante distinguir completamente esta fase del consumo posterior del histórico.

La validación previa:

```text
puede ejecutarse concurrentemente
y sus archivos pueden terminar fuera de orden
```

mientras que el consumo normal:

```text
debe continuar respetando
el orden cronológico del dataset
```

Por tanto:

```text
PARALLEL DATASET VALIDATION
        ≠
CHRONOLOGICAL DATA STREAM
```

Una vez terminados todos los lotes, sus resultados deben combinarse y ordenarse de forma determinista antes de tomar una decisión.

Conceptualmente:

```text
K workers
    ↓
K resultados parciales
    ↓
agregación
    ↓
orden determinista de archivos
    ↓
validación global
```

Si existen errores:

```text
agregar todos
→ ordenar
→ generar un único diagnóstico
→ abortar
```

Si no existen:

```text
dataset validado
→ comenzar enumeración normal
```

De esta manera podemos combinar:

```text
batches balanceados
+
paralelismo acotado
+
uso controlado de recursos
+
diagnóstico completo
+
resultado determinista
+
stream cronológico intacto
```

---

## Manejo de archivos que no cumplen ningún PhysicalRecordType

Independientemente del mercado, si uno o varios archivos no pueden validarse contra ninguno de los `PhysicalRecordType` soportados, debería producirse un error explícito y detenerse la ejecución correspondiente.

La validación no debería aplicar una estrategia de `first-fail-file`.

En su lugar, debería:

```text
obtener todos los archivos candidatos
    ↓
repartir su validación en lotes balanceados
con paralelismo acotado
    ↓
detectar el PRT de cada archivo
    ↓
agregar los resultados
    ↓
acumular todos los archivos inválidos
o incompatibles
    ↓
generar un diagnóstico único y completo
    ↓
abortar antes de comenzar el consumo del dataset
```

El hecho de validar concurrentemente no debe alterar el resultado funcional.

Todos los archivos relevantes deben quedar clasificados antes de comenzar el consumo normal del dataset y los errores deben agregarse posteriormente en un orden determinista.

No quiero que un archivo inválido sea interpretado silenciosamente como otro formato simplemente porque su tamaño resulta divisible por el tamaño de ese registro.

La validación debe identificar realmente el formato físico y, cuando exista un problema, permitir conocer en una sola ejecución todos los archivos que necesitan atención.

---

## Logging y diagnóstico

También quiero revisar cómo LEAN vanilla comunica este tipo de problemas.

LEAN y Auroboros son productos independientes: Auroboros utiliza LEAN como motor y mantiene customizaciones propias sobre él.

Por eso quiero analizar cómo LEAN registra actualmente información sobre su fuente de datos y su `DataFeed`, y decidir si nuestra customización binaria debería mejorar esos diagnósticos.

Por ejemplo, al iniciar una ejecución podría ser útil registrar de alguna manera clara el formato utilizado:

```text
DATA FORMAT: Binary
```

o una nomenclatura mejor alineada con los patrones de logging existentes en LEAN.

Si la validación física falla, quiero que el diagnóstico indique claramente **todos los archivos problemáticos detectados durante esa validación**, no únicamente el primero.

Si existe un único archivo inválido, conceptualmente podría verse así:

```text
DATA FORMAT ERROR:

<file_path_full>/12-FX_AUDCAD_5M_20150107-20150629.bin

is not a valid QuoteBarRow PhysicalRecordType.
```

Si existen varios archivos incompatibles, el diagnóstico debería listarlos conjuntamente:

```text
DATA FORMAT ERROR:

<file_path_full>/12-FX_AUDCAD_5M_20150107-20150629.bin
<file_path_full>/13-FX_AUDCAD_5M_20150630-20151220.bin
<file_path_full>/17-FX_AUDCAD_5M_20171201-20180522.bin

are not valid QuoteBarRow PhysicalRecordTypes.
```

La redacción definitiva deberá adaptarse a las convenciones reales de excepciones y logging de LEAN, incluyendo la manera más natural de expresar el singular y el plural.

Lo importante es que el diagnóstico permita conocer inmediatamente:

```text
todos los archivos afectados
+
formato físico detectado o inválido
+
formato requerido
+
razón por la que no pueden utilizarse
```

Aunque la validación interna se ejecute concurrentemente y los workers terminen en distinto orden, el diagnóstico no debería depender de ese orden de finalización.

Los archivos problemáticos deberían presentarse según un orden estable —idealmente el mismo orden cronológico/determinista utilizado por el resolver— para que dos ejecuciones sobre el mismo dataset produzcan el mismo diagnóstico.

La validación debería intentar proporcionar el diagnóstico completo antes de abortar, siempre que continuar examinando los demás archivos sea seguro y no tenga sentido detenerse inmediatamente por otra clase de error fatal.

En un mercado Forex, por ejemplo, debería quedar claro qué archivos concretos no cumplen el formato físico necesario para producir `QuoteBar`.

En un mercado que requiera `TradeBar`, si uno o varios archivos no coinciden con ninguno de los `PhysicalRecordType` capaces de producirlo, deberían informarse conjuntamente mediante el mismo criterio.

El objetivo es evitar una experiencia como:

```text
ejecución 1 → descubrir archivo inválido A
ejecución 2 → descubrir archivo inválido B
ejecución 3 → descubrir archivo inválido C
```

cuando el sistema podría haber detectado desde la primera validación:

```text
A
B
C
```

y comunicado los tres problemas de una sola vez.

---

## Resumen de mi propuesta

La idea central es dejar de utilizar el tipo solicitado por LEAN como una suposición implícita sobre el formato del archivo físico.

En su lugar:

```text
mercado
    ↓
tipo lógico requerido por LEAN
    ↓
archivos .bin candidatos
    ↓
determinar grado de paralelismo
    ↓
repartir archivos en lotes balanceados
    ↓
validar cada lote mediante
un número acotado de workers
    ↓
detectar el PhysicalRecordType
de cada archivo
    ↓
comprobar si cada PRT puede producir
el tipo lógico requerido
    ↓
agregar resultados
en orden determinista
    ↓
si existen incompatibilidades
→ informar todas y abortar

si no existen
→ comenzar el stream normal
```

Para mercados cuyo tipo requerido sea `TradeBar`, podrían coexistir archivos físicos `TradeBarRow` y `QuoteBarRow`, siempre que ambos tengan una proyección válida hacia `TradeBar`.

Para Forex, si `QuoteBar` es obligatorio, todos los archivos utilizados deberían cumplir el `PhysicalRecordType` capaz de producir correctamente un `QuoteBar`.

Si uno o varios no lo cumplen, la ejecución debería fallar, pero únicamente después de haber validado el conjunto de archivos y recopilado todos los incompatibles, de forma que los logs presenten un diagnóstico completo en una sola ejecución.

La validación debería estar diseñada para escalar desde pocos hasta miles de archivos mediante **lotes balanceados y un grado de paralelismo acotado**, evitando que el número de archivos determine directamente el número de threads o tareas concurrentes.

La arquitectura debería separar claramente:

```text
formato físico del archivo
≠
tipo lógico solicitado por LEAN
```

y también:

```text
validación paralela por lotes
≠
enumeración cronológica del stream
```

estableciendo formalmente cuáles conversiones entre ambos están permitidas.