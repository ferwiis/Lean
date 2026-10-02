# Auroboros Binary Pipeline
## Cuatro problemas arquitectónicos por resolver antes del SDD

### Propósito de este documento

La migración principal del pipeline binario ya está realizada: Auroboros pasó de materializar rangos históricos completos a consumir los archivos binarios mediante readers forward-only, memoria acotada e integración con los lifecycles nativos de LEAN.

La siguiente etapa no consiste en rediseñar nuevamente ese pipeline ni en comenzar todavía con multiprocessing.

El objetivo ahora es cerrar **cuatro problemas arquitectónicos principales** que agrupan los problemas individuales encontrados durante la auditoría posterior a la migración.

Este documento no sustituye al Handoff detallado. Su función es servir como **mesa de discusión previa al nuevo SDD**:

1. entender el problema completo;
2. decidir su semántica;
3. definir el comportamiento esperado;
4. acordar qué debe soportarse y qué debe rechazarse;
5. solo después convertir esas decisiones en un SDD prescriptivo para Codex.

---

# Problema 1 — Definir el contrato entre el dato binario físico y los tipos lógicos de LEAN

Este es el problema conceptual más importante.

Actualmente sabemos que los históricos Forex existentes contienen registros físicos de 64 bytes con OHLCV global y Bid/Ask OHLC. También sabemos que ese registro contiene información suficiente para construir un `QuoteBar` y, potencialmente, para proyectar su OHLCV global como `TradeBar`.

El problema es que el sistema todavía mezcla dos conceptos que deberían ser independientes:

```text
¿Qué existe físicamente en disco?
```

y:

```text
¿Qué tipo de dato está solicitando LEAN?
```

Un archivo no debería interpretarse como `TradeBarRow` simplemente porque LEAN pidió un `TradeBar`, ni como `QuoteBarRow` simplemente porque pidió un `QuoteBar`.

Primero debemos conocer qué schema físico contiene realmente el archivo y, después, determinar qué proyecciones lógicas permite ese schema.

Este bloque engloba los problemas relacionados con:

- separación entre `PhysicalRecordType` y `RequestedLeanType`;
- identificación/versionado del schema físico;
- proyección `QuoteBarRow → QuoteBar`;
- posible proyección `QuoteBarRow → TradeBar`;
- semántica correcta de `QuoteBar.Value`;
- significado y tratamiento del fallback Bid/Ask sintético;
- situación de `TradeBarRow`, cuyo ABI existe pero todavía no tiene un histórico real equivalente validado;
- alcance actual Forex frente a futuras extensiones a otros mercados.

## Qué tenemos que resolver

Necesitamos convertir el formato binario en un **contrato explícito**, no en una colección de suposiciones implícitas derivadas del tipo solicitado por LEAN.

El pipeline debería poder responder claramente:

```text
Archivo físico
    ↓
¿Qué schema contiene?
    ↓
¿Qué semántica tiene ese schema?
    ↓
¿Qué tipos LEAN puede producir legítimamente?
```

También debemos establecer qué información del registro es canónica para cada proyección.

Por ejemplo, en `QuoteBarRow` existen simultáneamente:

```text
OHLCV global
Bid OHLC
Ask OHLC
```

Debemos decidir qué representa cada familia de campos y qué campos son la fuente de verdad de un `QuoteBar`.

---

# Solución arquitectónica acordada para el Problema 1

## 1. Principio central: separar almacenamiento físico de representación lógica

La solución parte de una regla fundamental:

```text
PhysicalRecordType
        ≠
RequestedLeanType
```

El `PhysicalRecordType` describe exclusivamente **cómo están organizados los bytes dentro de un archivo `.bin`**.

El tipo lógico de LEAN describe **qué objeto necesita recibir el motor** para una determinada suscripción, mercado o request.

La resolución conceptual pasa a ser:

```text
archivo físico
    ↓
resolver PhysicalRecordType
    ↓
comprobar compatibilidad con el mercado/request
    ↓
seleccionar proyección permitida
    ↓
producir el tipo lógico de LEAN
```

---

## 2. PhysicalRecordTypes iniciales

```text
TradeBarRow
QuoteBarRow
```

Los tamaños pertenecen al contrato físico y no al nombre:

```text
TradeBarRow → record de 32 bytes
QuoteBarRow → record de 64 bytes
```

### TradeBarRow

```text
Timestamp
OHLCV
```

Proyección:

```text
TradeBarRow
    └──→ TradeBar
```

### QuoteBarRow

```text
Timestamp
OHLCV global
Bid OHLC
Ask OHLC
Reserved
```

Proyecciones:

```text
QuoteBarRow
    ├──→ QuoteBar
    └──→ TradeBar
```

Para `TradeBar`, el OHLCV global constituye la fuente lógica.

Para `QuoteBar`, Bid/Ask gobiernan la semántica LEAN:

```text
Bid OHLC + Ask OHLC
        ↓
QuoteBar
        ↓
QuoteBar.Close
        ↓
QuoteBar.Value
```

El `Close` global no debe sobrescribir `QuoteBar.Value`.

---

## 3. Un único PhysicalRecordType por archivo

Cada `.bin` contiene exactamente un PRT de principio a fin.

Válido:

```text
archivo A → TradeBarRow
archivo B → QuoteBarRow
```

Inválido:

```text
archivo C
    QuoteBarRow
    QuoteBarRow
    TradeBarRow
    QuoteBarRow
```

Esta restricción conserva el carácter fixed-width del formato.

---

## 4. Mezcla entre archivos

Un mismo dataset puede utilizar archivos de distinto PRT cuando todos puedan proyectarse legítimamente al tipo solicitado.

```text
TradeBarRow → TradeBar
QuoteBarRow → TradeBar
```

permite una request `TradeBar` sobre archivos mixtos.

En cambio:

```text
TradeBarRow ✗ QuoteBar
QuoteBarRow ✓ QuoteBar
```

impide utilizar `TradeBarRow` para una request `QuoteBar`.

---

## 5. Contrato por mercado

El mercado reduce los PRT legalmente posibles.

Ejemplo:

```text
Forex
    ↓
QuoteBar
    ↓
{ QuoteBarRow }
```

Si solo queda un PRT posible, no se necesita inspeccionar el payload para identificarlo.

---

## 6. `_TB` y `_QB`

Los sufijos son opcionales:

```text
_TB → TradeBarRow
_QB → QuoteBarRow
```

No son obligatorios para archivos legacy.

Cuando existen:

- son una declaración contractual;
- deben ser coherentes con el mercado;
- evitan inferencia;
- facilitan diagnóstico;
- no certifican la validez de todos los records.

---

## 7. Papel de `fileLength`

No identifica de forma general el PRT porque:

```text
64 = 2 × 32
```

y por tanto:

```text
fileLength % 32 == 0
fileLength % 64 == 0
```

puede ser ambiguo.

Sí puede descartar algunos candidatos:

```text
% 32 == 0
% 64 != 0
→ no puede ser QuoteBarRow
```

Una vez resuelto el PRT:

```text
fileLength % recordSize == 0
```

se convierte en una validación estructural O(1).

También permite:

```text
recordCount = fileLength / recordSize
```

---

## 8. Probe de 128 bytes ante ambigüedad real

Solo cuando:

```text
varios PRT legales
+
sin _TB/_QB
+
fileLength no resuelve
```

se leen los primeros:

```text
128 bytes
```

y se prueban simultáneamente como:

```text
4 × TradeBarRow
2 × QuoteBarRow
```

sin construir objetos LEAN.

Resultado:

```text
solo TradeBarRow válido
→ TradeBarRow

solo QuoteBarRow válido
→ QuoteBarRow

ninguno válido
→ formato inválido

ambos válidos
→ formato ambiguo
```

Nunca se adivina.

---

## 9. No full scan ni sampling distribuido

No se realizará:

```text
inicio + medio + final
```

ni:

```text
scan completo previo
```

El primero no garantiza integridad.

El segundo introduce O(R) antes de comenzar y puede terminar leyendo los mismos datos dos veces.

---

## 10. Validación durante `MoveNext()`

```text
MoveNext()
    ↓
leer record
    ↓
validarlo
    ↓
proyectarlo
    ↓
emitirlo
```

La corrupción profunda se detecta cuando realmente se alcanza.

El diagnóstico debe poder incluir conceptualmente:

```text
archivo
record index
byte offset
PhysicalRecordType
razón
```

---

# Árbol general del Problema 1

```text
ARCHIVO
    ↓
contexto del filename
    ↓
contrato del mercado
    ↓
resolver PhysicalRecordType
    ↓
validación estructural
    ↓
compatibilidad PRT → tipo lógico
    ↓
proyección
    ↓
streaming
    ↓
validación record-by-record
```

---

# Complejidad del Problema 1

Para `F` archivos:

```text
resolución general → O(F)
```

Por archivo:

```text
resolución PRT → O(1)
fileLength      → O(1)
probe           → O(1)
memoria         → O(1)
```

Durante consumo de `R` records:

```text
tiempo → O(R)
memoria → O(1)
```

Una garantía semántica completa previa requeriría trabajo proporcional al número total de records y deliberadamente no forma parte del startup normal.

---

# Semántica final de QuoteBar

```text
QuoteBarRow
      │
 ┌────┴────┐
 │         │
OHLCV   Bid/Ask
 │         │
 ▼         ▼
TradeBar QuoteBar
             │
             ▼
     Value = Close vanilla
```

El fallback sintético de Bid/Ask pertenece a Data Engineering/ingestion.

Una vez escrito un `QuoteBarRow` válido, el backend:

- no inventa otro spread;
- no sustituye Bid/Ask por el OHLCV global;
- no modifica `QuoteBar` para incluir provenance custom.

Si en el futuro se necesita distinguir quote observado de reconstruido, será una capacidad independiente de Data Engineering/metadata.

**FINALIZADO ANÁLISIS SOLUCIÓN PROBLEMA 1**

---

# Problema 2 — Definir cómo una request de LEAN se resuelve contra los datasets disponibles

Una vez establecido qué representa físicamente cada archivo, la siguiente responsabilidad es decidir **qué fuente utilizar para responder una request concreta de LEAN**.

El problema puede resumirse como:

```text
LEAN Request
+
datasets físicos disponibles
        ↓
¿qué combinación válida debe ejecutarse?
```

La decisión debe ser determinista.

No es aceptable entregar algún dato solamente porque técnicamente pueda producirse.

El dato producido debe conservar la semántica solicitada.

---

# Solución arquitectónica acordada para el Problema 2

## 1. La request lógica continúa siendo autoridad

El resolver no debe cambiar silenciosamente:

```text
RequestedLeanType
RequestedPeriod
SecurityType
```

para adaptarlos a los archivos disponibles.

Su trabajo es encontrar una forma legítima de satisfacer la request.

Conceptualmente:

```text
Resolve(
    RequestedLeanType,
    RequestedPeriod,
    SecurityType,
    AvailableDatasets
)
```

debe producir:

```text
PhysicalRecordType
Projection
NativePeriod
ConsolidationPeriod
```

o:

```text
UNSUPPORTED REQUEST
```

---

## 2. SecurityType define semántica y restricciones, pero no debe falsificar una request explícita

Para llamadas internas de Auroboros debe utilizarse el tipo lógico natural de LEAN para el mercado.

Ejemplo importante:

```text
Forex
→ QuoteBar
```

Por tanto, una lógica interna que realmente necesita datos Forex debe solicitar explícitamente `QuoteBar` y no utilizar accidentalmente un overload que implique `TradeBar`.

Sin embargo, si una request explícita solicita `TradeBar` y existe una proyección legítima:

```text
QuoteBarRow → TradeBar
```

esa request puede satisfacerse.

La regla es:

```text
no convertir una request por accidente
pero tampoco prohibir una proyección expresamente soportada
```

---

## 3. Orden de resolución de una request

El flujo conceptual será:

```text
LEAN REQUEST
    ↓
1. identificar mercado/símbolo/rango
    ↓
2. descubrir datasets candidatos
    ↓
3. resolver PRT de cada archivo
    ↓
4. eliminar PRT incompatibles con RequestedLeanType
    ↓
5. agrupar por NativePeriod
    ↓
6. seleccionar periodo nativo compatible
    ↓
7. determinar consolidación necesaria
    ↓
8. construir el stream
```

---

## 4. Selección del timeframe nativo

Se establece la siguiente prioridad:

```text
1. exact match
2. si no existe:
   mayor periodo nativo
   menor que el solicitado
   que lo divida exactamente
```

Ejemplo:

```text
disponibles:
5M
15M

request:
30M
```

resultado:

```text
15M → 30M
```

en lugar de:

```text
5M → 30M
```

porque requiere menos records de entrada.

---

## 5. Invariante necesario para utilizar el periodo compatible más cercano

Esta optimización es válida únicamente si los distintos timeframes de una misma familia de datos respetan el mismo contrato de agregación.

Debe cumplirse conceptualmente:

```text
Aggregate(5M → 30M)
==
Aggregate(15M → 30M)
==
30M nativo
```

dentro de las reglas de precisión y semántica correspondientes.

Esto implica:

- mismas fronteras temporales;
- misma timezone base;
- misma fuente semántica;
- OHLC compatible;
- volumen compatible;
- mismo tratamiento de gaps;
- misma política Bid/Ask.

Los datasets existentes deberán caracterizarse con tests.

Si una familia de datos viola este contrato, sus diferentes timeframes **no pueden considerarse intercambiables** bajo esta política.

La optimización nunca debe ocultar diferencias semánticas.

---

## 6. Request más fina que el dataset disponible

Ejemplo:

```text
dataset mínimo disponible = 5M
request                 = 1M
```

La respuesta oficial será:

```text
REJECT
```

No se realizará:

```text
5M → fingir 1M
```

ni se cambiará silenciosamente la configuración de LEAN.

No existe información suficiente para reconstruir legítimamente cuatro barras adicionales dentro de cada barra 5M.

---

## 7. Periodos incompatibles

También se rechazará una request cuando ningún periodo disponible pueda dividir limpiamente el periodo solicitado.

Ejemplo:

```text
dataset = 5M
request = 7M
```

Un record 5M puede atravesar la frontera de una barra 7M y no puede dividirse sin inventar información.

Resultado:

```text
UNSUPPORTED PERIOD RELATION
```

---

## 8. Consolidación

Cuando:

```text
NativePeriod < RequestedPeriod
```

y:

```text
RequestedPeriod % NativePeriod == 0
```

la consolidación debe realizarse utilizando los mecanismos nativos de LEAN siempre que sea posible.

El backend binario debe proporcionar el stream raw correcto.

No debe duplicar innecesariamente la lógica temporal de LEAN.

---

## 9. Datasets mixtos

Si una request `TradeBar` selecciona un periodo en el que existen:

```text
archivo A → TradeBarRow
archivo B → QuoteBarRow
```

ambos pueden participar en el mismo stream lógico cuando:

```text
TradeBarRow → TradeBar
QuoteBarRow → TradeBar
```

son proyecciones válidas.

El resolver trabaja por compatibilidad semántica, no exige homogeneidad física entre archivos.

---

# Árbol general del Problema 2

```text
LEAN REQUEST
    ↓
tipo lógico + periodo + mercado
    ↓
datasets candidatos
    ↓
filtrar por compatibilidad PRT
    ↓
buscar periodo exacto
    │
    ├─ existe → usarlo
    │
    └─ no existe
         ↓
    buscar mayor divisor nativo compatible
         │
         ├─ existe → consolidar
         └─ no existe → REJECT
```

---

# Política de error del Problema 2

Debe rechazarse explícitamente:

- tipo lógico sin proyección disponible;
- request más fina que todos los datasets;
- relación temporal no divisible;
- dataset con timeframe incompatible;
- PRT incompatible con la request;
- combinación ambigua que no pueda resolverse determinísticamente.

Nunca debe devolverse un stream cuya semántica contradiga la `SubscriptionDataConfig` observada por LEAN.

---

# Complejidad del Problema 2

Para `F` archivos candidatos:

```text
discovery/resolution → O(F)
```

La selección del timeframe opera sobre el conjunto de periodos disponibles y es despreciable frente al recorrido de archivos.

Si:

```text
Rfine
```

es la cantidad de records que habría que leer desde la fuente más fina y:

```text
Rselected
```

los records del periodo nativo seleccionado:

```text
Rselected ≤ Rfine
```

Elegir el divisor compatible más cercano reduce:

- I/O;
- parsing;
- allocations;
- consolidación;
- llamadas a `MoveNext()`.

La memoria sigue siendo acotada respecto al histórico.

---

# Tests conceptuales obligatorios del Problema 2

```text
exact native period
5M → 30M
15M → 30M
1M request sobre 5M → fail
7M request sobre 5M → fail
Forex QuoteBar History
explicit TradeBar projection from QuoteBarRow
mixed TradeBarRow / QuoteBarRow → TradeBar
no compatible logical projection → fail
```

Además debe existir una caracterización que demuestre equivalencia de agregación entre timeframes antes de asumirlos intercambiables.

**FINALIZADO ANÁLISIS SOLUCIÓN PROBLEMA 2**

---

# Problema 3 — Garantizar la integridad temporal del stream dentro del lifecycle completo de LEAN

Una vez sabemos:

```text
qué archivo leer
qué PRT contiene
qué tipo lógico producir
qué timeframe utilizar
```

queda el problema más delicado de integración:

```text
¿qué secuencia observa finalmente LEAN?
```

El stream debe preservar:

```text
orden
completitud
unicidad
semántica temporal
```

a través de:

- límites entre archivos;
- overlaps;
- History;
- DataFeed;
- warmup;
- fill-forward;
- timezone;
- DST;
- lifecycle normal de LEAN.

---

# Solución arquitectónica acordada para el Problema 3

## 1. No depender de `first-file-wins` para ocultar overlaps

La política anterior:

```text
si timestamp <= último emitido
→ descartar
```

es insuficiente.

Puede eliminar información legítima cuando dos archivos contienen gaps complementarios.

Ejemplo:

```text
Archivo A:
00:00  00:05        00:15

Archivo B:
       00:05  00:10 00:15
```

Un recorrido secuencial de A seguido de B perdería:

```text
00:10
```

si simplemente descarta todo timestamp anterior a `00:15`.

La solución no debe depender de que Data Engineering nunca produzca este escenario.

---

## 2. Merge cronológico streaming

Los archivos que se solapen temporalmente deberán combinarse mediante un **merge cronológico acotado**.

Conceptualmente:

```text
archivo A ──┐
archivo B ──┼──→ chronological merge → stream LEAN
archivo C ──┘
```

El merge mantiene únicamente el próximo record necesario de cada fuente activa.

No materializa archivos completos.

---

## 3. Unidad de memoria: un elemento pendiente por fuente activa

Si:

```text
K = número máximo de archivos simultáneamente solapados
```

el merge necesita conceptualmente:

```text
O(K)
```

memoria.

No necesita:

```text
O(total records)
```

Cuando no existen overlaps:

```text
K = 1
```

y el comportamiento se aproxima al reader streaming simple.

---

## 4. Orden de emisión

Entre los próximos records disponibles de las fuentes activas se emite:

```text
el timestamp más antiguo
```

Después únicamente avanza la fuente de la que salió ese record.

Conceptualmente:

```text
next(A) = 00:05
next(B) = 00:10
next(C) = 00:07

emitir 00:05
```

y continuar.

---

## 5. Timestamps duplicados

Cuando dos fuentes producen exactamente el mismo timestamp para el mismo stream lógico:

```text
timestamp A == timestamp B
```

deben compararse semánticamente después de la proyección correspondiente.

### Duplicado coherente

```text
timestamp igual
+
dato lógico equivalente
```

resultado:

```text
emitir una sola vez
```

### Duplicado conflictivo

```text
timestamp igual
+
valores diferentes
```

resultado:

```text
DATA CONFLICT ERROR
```

No se utilizará una política silenciosa de:

```text
first-file-wins
```

cuando ambos archivos discrepen sobre el dato.

Esto evita que el orden de filenames determine silenciosamente el precio utilizado por el backtest.

---

## 6. Mixed PRT durante el merge

La comparación debe realizarse sobre la representación lógica solicitada.

Ejemplo:

```text
archivo A
TradeBarRow
    ↓
TradeBar

archivo B
QuoteBarRow
    ↓
TradeBar
```

Si ambos producen el mismo timestamp, la comparación relevante es:

```text
TradeBar vs TradeBar
```

y no una comparación byte-a-byte entre layouts físicos diferentes.

---

## 7. Orden dentro de cada archivo

Cada archivo individual debe continuar siendo monotónico por timestamp.

Si dentro de una misma fuente aparece:

```text
T[n+1] < T[n]
```

el archivo es inválido.

El merge entre archivos no debe utilizarse para reparar archivos internamente desordenados.

---

## 8. Responsabilidad del backend binario frente a LEAN vanilla

La capa binaria debe limitarse a producir un stream raw correcto, ordenado y semánticamente válido.

Después de eso deben mantenerse los wrappers y lifecycles nativos de LEAN para:

```text
fill-forward
exchange hours
mapping
normalization
warmup
LastPointTracker
timezone handling
consolidation
```

cuando corresponda.

El backend binario no debe duplicar esas responsabilidades.

---

## 9. History y DataFeed deben compartir la misma semántica raw

La fuente utilizada por:

```text
History
```

y la utilizada por:

```text
DataFeed
```

deben atravesar la misma lógica fundamental de:

```text
resolver
PRT
projection
merge
chronology
```

antes de divergir hacia los wrappers específicos del lifecycle.

Dos requests equivalentes no deben producir precios distintos porque una entró por History y otra por DataFeed.

---

## 10. Warmup → normal

El paso:

```text
warmup
    ↓
normal execution
```

no debe producir:

- duplicación del último punto;
- pérdida del primer punto normal;
- retroceso temporal.

La capa binaria no debe mantener estado global compartido entre ambos lifecycles.

La frontera debe continuar siendo gestionada mediante el lifecycle nativo de LEAN y sus mecanismos de tracking.

La solución deberá demostrarse con test end-to-end.

---

## 11. Fill-forward

Fill-forward no pertenece al reader físico.

El orden correcto es:

```text
binary raw stream
    ↓
merge / dedupe
    ↓
projection
    ↓
native LEAN wrappers
    ↓
fill-forward
```

No debe fabricarse fill-forward directamente desde el `.bin`.

---

## 12. Timezone y DST

Los timestamps físicos continúan representando tiempo UTC según el ABI actual.

La conversión hacia:

```text
DataTimeZone
ExchangeTimeZone
```

debe delegarse a las abstracciones temporales nativas de LEAN.

No deben añadirse tablas custom de DST dentro del backend binario.

El backend debe demostrar que conserva correctamente la transición alrededor de cambios DST.

---

# Árbol general del Problema 3

```text
ARCHIVOS SELECCIONADOS
        ↓
seek al rango requerido
        ↓
reader independiente por archivo activo
        ↓
proyección lógica
        ↓
chronological merge
        ↓
dedupe de timestamps equivalentes
        ↓
conflict detection
        ↓
stream raw ordenado
        ↓
wrappers nativos LEAN
        ↓
History / DataFeed / warmup / fill-forward
```

---

# Complejidad del Problema 3

Sea:

```text
R = records consumidos
K = máximo de archivos simultáneamente activos
```

El merge mediante selección eficiente del menor timestamp puede operar en:

```text
Tiempo:
O(R log K)

Memoria:
O(K)
```

En el caso común sin overlap:

```text
K = 1
```

el coste se aproxima a:

```text
O(R)
```

La memoria continúa siendo independiente del tamaño total del histórico.

### Trade-off

Se acepta una pequeña complejidad adicional frente al simple recorrido secuencial porque se obtiene:

- ausencia de pérdida de gaps complementarios;
- orden cronológico real;
- deduplicación correcta;
- detección de conflictos;
- menor dependencia de supuestos externos de Data Engineering.

---

# Política de error del Problema 3

Debe fallarse cuando exista:

```text
timestamp decreciente dentro de un archivo
duplicate timestamp con valores conflictivos
stream imposible de ordenar bajo el contrato
error temporal o de proyección
```

Los duplicates equivalentes se deduplican.

Los conflicts no se silencian.

---

# Tests conceptuales obligatorios del Problema 3

```text
file boundary sin overlap
overlap idéntico
overlap complementario
overlap conflictivo
3+ archivos solapados
mixed PRT con misma proyección lógica
timestamp decreciente dentro de archivo
History vs DataFeed equivalentes
warmup → normal
fill-forward
DST forward
DST backward
early disposal
```

**FINALIZADO ANÁLISIS SOLUCIÓN PROBLEMA 3**

---

# Problema 4 — Cerrar rendimiento, validación y mantenibilidad del pipeline

Una vez cerrada la semántica de los tres problemas anteriores, la última etapa consiste en eliminar costes evitables y dejar el fork en condiciones mantenibles.

Este bloque cubre:

```text
seek
benchmark
upstream sync
tests finales
documentación
patch cleanup
```

No modifica la semántica definida anteriormente.

---

# Solución arquitectónica acordada para el Problema 4

## 1. El reader no debe recorrer necesariamente desde record 0

El formato ofrece dos propiedades:

```text
record size fijo
timestamps ordenados
```

Por tanto no existe razón arquitectónica para que una request situada cerca del final del archivo tenga que recorrer todas las barras anteriores.

---

## 2. Binary search directo sobre timestamps

La optimización inicial será:

```text
binary search
```

sobre el propio archivo.

No se introducirá inicialmente:

```text
índice persistente
base de datos auxiliar
sidecar temporal
cache distribuida
```

La posición física continúa siendo calculable mediante:

```text
offset = recordIndex * recordSize
```

y el timestamp se encuentra en una posición conocida dentro del record.

---

## 3. Complejidad del seek

Para un archivo con:

```text
N records
```

buscar el primer record relevante pasa conceptualmente de:

```text
sequential scan
O(N)
```

a:

```text
binary search
O(log N)
```

con:

```text
O(1) memoria adicional
```

Después de encontrar el punto inicial continúa el recorrido forward-only habitual.

---

## 4. El seek debe respetar la ventana realmente necesaria

El objetivo no es simplemente buscar:

```text
primer timestamp >= StartUtc
```

sin contexto.

Si la consolidación o el lifecycle requiere comenzar ligeramente antes para construir correctamente la primera barra lógica, el resolver debe determinar primero la ventana raw necesaria.

Después:

```text
seek → inicio raw necesario
```

Los detalles exactos pertenecen al SDD.

---

## 5. Un seek independiente por archivo participante

En un dataset multi-file:

```text
archivo A
archivo B
archivo C
```

cada reader puede posicionarse de forma independiente antes de entrar al merge cronológico.

Esto evita escanear meses de datos previos dentro de cada archivo.

---

## 6. No persistent index inicialmente

Un índice adicional introduce:

- lifecycle propio;
- versionado;
- invalidación;
- sincronización;
- storage;
- posibilidad de metadata stale.

Mientras el timestamp pueda encontrarse mediante binary search sobre records fixed-width, ese coste no está justificado.

Un índice solo deberá reconsiderarse si profiling real demuestra que:

```text
O(log N)
```

sigue siendo un cuello de botella relevante.

---

## 7. Benchmark controlado

Debe existir una comparación reproducible:

```text
OLD BinaryDataLoader
vs
NEW Binary Streaming Pipeline
```

manteniendo:

```text
mismo dataset
mismo rango
mismo workload
mismo hardware
misma configuración
```

Métricas mínimas:

```text
elapsed time
time-to-first-bar
managed heap
working set
private bytes
allocations
GC Gen0
GC Gen1
GC Gen2
```

Cuando sea útil también deberán observarse:

```text
page faults
I/O leído
CPU time
```

---

## 8. No inventar objetivos numéricos

No se establecerá anticipadamente:

```text
5x
6x
10x
```

como criterio de éxito.

Primero se medirá.

El resultado deberá reportar números reales.

La mejora arquitectónica que sí puede exigirse sin benchmark es:

```text
memoria retenida no proporcional
al número total de records históricos
```

es decir, eliminar el antiguo patrón de materialización O(N).

---

## 9. Criterios de aceptación de rendimiento

El nuevo pipeline debe demostrar:

```text
correctness primero
memoria acotada
time-to-first-bar razonable
sin regresión patológica de elapsed time
```

El benchmark será utilizado para cuantificar, no para justificar afirmaciones previamente decididas.

Si una optimización aumenta complejidad sin beneficio medible, deberá eliminarse o posponerse.

---

## 10. Multiprocessing continúa fuera de alcance

No se implementará todavía:

```text
shared memory
IPC
data service central
cache cross-process
multiprocessing redesign
```

Primero debe existir un reader individual:

```text
correcto
estable
streaming
medido
```

Después podrá perfilarse:

```text
1
2
4
8
16 procesos
```

y decidir si existe un problema cross-process real.

---

## 11. Sincronización con upstream

Antes de cerrar la implementación definitiva:

```text
QuantConnect/Lean upstream master
        ↓
fork master
        ↓
auroboros
```

debe volver a quedar ordenado.

Proceso conceptual:

```text
1. actualizar fork master contra upstream
2. colocar/rebasear auroboros sobre ese master
3. resolver conflictos
4. compilar
5. ejecutar suite baseline
6. implementar/cerrar patch final
7. ejecutar suite completa nuevamente
```

No debe desarrollarse el cierre definitivo sobre una base innecesariamente atrasada.

---

## 12. Patch stack

Durante investigación puede mantenerse:

```text
commits temporales
fixups
documentos de auditoría
```

para facilitar revisión.

Una vez:

```text
semántica cerrada
+
implementación terminada
+
tests pasando
+
benchmark ejecutado
+
upstream sync completado
```

se hará limpieza.

---

## 13. Squash/fixup final

El objetivo final no es conservar la historia accidental de la investigación.

El fork debe terminar con un patch custom entendible sobre LEAN.

Antes de hacer squash:

```text
NO
```

Después de validación completa:

```text
SÍ
```

La estructura exacta de commits finales podrá decidirse según responsabilidades técnicas, pero debe evitar una pila de commits temporales sin valor histórico.

---

## 14. Documentación

Se conservará permanentemente únicamente la documentación que explique:

```text
contrato binario
arquitectura
extensiones deliberadas sobre LEAN
operación necesaria
```

Los documentos cuyo único propósito haya sido:

```text
auditoría temporal
brainstorming
handoff intermedio
```

pueden eliminarse del patch final una vez su contenido relevante haya sido absorbido por documentación estable y SDD/resultados.

---

# Árbol general del Problema 4

```text
CORRECCIÓN CERRADA
        ↓
sync upstream
        ↓
baseline tests
        ↓
binary-search seek
        ↓
suite correctness
        ↓
benchmark controlado
        ↓
analizar resultados
        ↓
limpiar documentación
        ↓
fixup / squash
        ↓
PATCH AUROBOROS LIMPIO
```

---

# Complejidad final esperada

## Descubrimiento de archivos

```text
O(F)
```

## Resolución PRT

```text
O(1) por archivo
```

## Seek inicial

Para archivo con `N` records:

```text
O(log N)
```

## Streaming de records usados

```text
O(R)
```

## Merge con overlaps

```text
O(R log K)
```

donde `K` es el máximo número de archivos activos simultáneamente.

## Memoria del reader

Sin overlaps:

```text
O(1)
```

Con merge:

```text
O(K)
```

Nunca:

```text
O(total historical records)
```

por diseño normal del pipeline.

---

# Tests y mediciones finales obligatorias

La suite final debe cubrir conjuntamente:

```text
TradeBarRow
QuoteBarRow

TradeBar projection
QuoteBar projection

single file
multi-file
mixed PRT

exact timeframe
consolidation
unsupported finer request
non-divisible period

overlap identical
overlap complementary
overlap conflict

History
DataFeed
warmup
fill-forward
DST

deep-range seek
early disposal

real Forex dataset characterization
```

Y posteriormente:

```text
old-vs-new benchmark
```

---

# Criterio de cierre del Problema 4

El problema se considera resuelto cuando:

```text
correctness suite pasa
+
real-data characterization pasa
+
upstream está sincronizado
+
seek no requiere scan lineal completo
+
benchmark está documentado
+
no existen claims de rendimiento inventados
+
patch stack queda limpio
```

**FINALIZADO ANÁLISIS SOLUCIÓN PROBLEMA 4**

---

# Estado final del Pre-SDD

Los cuatro bloques arquitectónicos ya tienen decisiones explícitas:

```text
1. Binary Data Contract
        ✓
2. LEAN Request Resolution
        ✓
3. Stream & Lifecycle Integrity
        ✓
4. Performance & Engineering Closure
        ✓
```

La siguiente etapa ya no consiste en debatir qué debería hacer Auroboros.

Consiste en transformar estas decisiones en un SDD prescriptivo.

El SDD deberá contener al menos:

```text
CURRENT STATE
SUPPORTED SCOPE
BINARY ABI
INVARIANTS
EXACT SEMANTICS
FILES ALLOWED TO CHANGE
FILES NOT TO CHANGE
IMPLEMENTATION REQUIREMENTS
ERROR POLICY
TEST MATRIX
REAL-DATA CHARACTERIZATION
PERFORMANCE ACCEPTANCE
BUILD/TEST COMMANDS
NON-GOALS
COMPLETION CHECKLIST
```

Codex no deberá recibir preguntas arquitectónicas abiertas.

Deberá recibir una especificación cerrada y limitarse a:

```text
implementar
probar
medir
reportar
```