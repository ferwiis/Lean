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

Un archivo no debería interpretarse como TradeBar32 simplemente porque LEAN pidió un `TradeBar`, ni como FxQuote64 simplemente porque pidió un `QuoteBar`.

Primero debemos conocer qué schema físico contiene realmente el archivo y, después, determinar qué proyecciones lógicas permite ese schema.

Este bloque engloba los problemas relacionados con:

- separación entre `PhysicalRecordType` y `RequestedLeanType`;
- identificación/versionado del schema físico;
- proyección `FxQuote64 → QuoteBar`;
- posible proyección `FxQuote64 → TradeBar`;
- semántica correcta de `QuoteBar.Value`;
- significado y tratamiento del fallback Bid/Ask sintético;
- situación de `TradeBarRow32`, cuyo ABI existe pero todavía no tiene un histórico real equivalente validado;
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

Por ejemplo, en `FxQuote64` existen simultáneamente:

```text
OHLCV global
Bid OHLC
Ask OHLC
```

Debemos decidir qué representa cada familia de campos y qué campos son la fuente de verdad de un `QuoteBar`.

## Preguntas que deben quedar respondidas

- ¿Cómo identificará el runtime el schema físico de un archivo?
- ¿Debe existir un identificador/versionado explícito del ABI?
- ¿`FxQuote64` soportará formalmente tanto `QuoteBar` como `TradeBar`?
- Si soporta `TradeBar`, ¿el OHLCV global será la proyección oficial?
- ¿`QuoteBar.Value` debe corresponder al precio derivado de Bid/Ask según la semántica normal de LEAN?
- ¿Qué significado tiene entonces el OHLCV global cuando el registro se proyecta como `QuoteBar`?
- ¿El Bid/Ask sintético generado por Data Engineering forma parte oficial del contrato?
- ¿Necesitamos distinguir de alguna forma quotes reales de quotes reconstruidos?
- ¿`TradeBar32` se mantiene como schema soportado para datasets futuros aunque todavía no tengamos un histórico real para caracterizarlo?
- ¿El contrato actual debe declararse explícitamente Forex-only?
- ¿Cómo dejamos preparada la arquitectura para otros mercados sin afirmar soporte que todavía no existe?

## Ideas iniciales para discutir

Una dirección posible sería introducir conceptualmente tres niveles independientes:

```text
Physical schema
    ↓
Semantic record
    ↓
LEAN projection
```

Por ejemplo:

```text
FxQuoteRecord64
    ├──→ QuoteBar
    └──→ TradeBar
```

mientras:

```text
TradeBarRecord32
    └──→ TradeBar
```

El identificador del schema físico podría convertirse en una propiedad del dataset y no del request LEAN.

La decisión exacta de cómo identificarlo —manifest, metadata, filename, header u otra regla contractual— queda abierta para discusión.

---

# Problema 2 — Definir cómo una request de LEAN se resuelve contra los datasets disponibles

Una vez definido qué contienen realmente nuestros archivos, aparece una segunda cuestión distinta:

```text
LEAN pide algo.
¿Qué dataset físico debemos utilizar para responder?
```

Aquí ya no estamos definiendo el formato binario.

Estamos definiendo la **política de resolución de requests**.

Este bloque agrupa los problemas relacionados con:

- History solicitando `TradeBar` cuando Forex normalmente requiere `QuoteBar`;
- elección del timeframe nativo cuando existen varias fuentes compatibles;
- comportamiento cuando LEAN solicita una resolución más fina que la disponible.

## Qué tenemos que resolver

La factory/resolver necesita una política determinista que transforme:

```text
LEAN Request
+
datasets disponibles
```

en:

```text
schema físico seleccionado
+
proyección lógica
+
timeframe nativo
+
consolidación necesaria
```

o, cuando la request no pueda satisfacerse correctamente:

```text
rechazo explícito
```

Actualmente existen casos en los que el pipeline consigue producir algún dato, pero eso no garantiza que la semántica sea correcta para la request original.

Un ejemplo importante es:

```text
dataset disponible = 5M
LEAN request        = 1M
```

Entregar barras 5M mientras LEAN continúa creyendo que trabaja con una configuración 1M puede ser peor que rechazar la request.

También debemos decidir qué ocurre cuando existen varias fuentes capaces de producir el mismo resultado:

```text
5M
15M
    ↓
request 30M
```

Ambas pueden consolidarse a 30M, pero tienen costes y posiblemente implicaciones distintas.

## Preguntas que deben quedar respondidas

- Para Forex, ¿qué tipo debe solicitar Auroboros cuando necesita History?
- ¿Debe corregirse cualquier uso interno que solicite implícitamente `TradeBar` cuando semánticamente necesita `QuoteBar`?
- Si una request puede satisfacerse con distintos timeframes nativos, ¿cuál debemos elegir?
- ¿Preferimos la fuente más fina disponible o la fuente compatible más cercana al periodo solicitado?
- ¿Existe alguna diferencia semántica real entre consolidar `5M → 30M` y `15M → 30M` con nuestros datos?
- ¿Qué hacemos cuando el request es más fino que cualquier dataset disponible?
- ¿Rechazamos explícitamente?
- ¿Normalizamos la request?
- ¿Existe alguna forma legítima de soportarlo sin fabricar información inexistente?
- ¿Qué combinaciones de tipo lógico, schema físico y periodo deben considerarse incompatibles?

## Ideas iniciales para discutir

La resolución podría formalizarse conceptualmente como una función:

```text
Resolve(
    RequestedLeanType,
    RequestedPeriod,
    SecurityType,
    AvailableDatasets
)
```

que produzca algo semejante a:

```text
PhysicalSchema
Projection
NativePeriod
ConsolidationPeriod
```

La función debería ser determinista y debería fallar cuando la request no tenga una interpretación semánticamente válida.

Una posible política de timeframe sería utilizar:

```text
el mayor periodo nativo
que divide exactamente
el periodo solicitado
```

para reducir lectura y construcción de objetos.

Pero esa política solo debería aprobarse después de demostrar que no altera la semántica respecto a utilizar datos más finos.

---

# Problema 3 — Garantizar la integridad temporal del stream dentro del lifecycle completo de LEAN

Una vez sabemos qué datos leer y cómo resolver una request, necesitamos garantizar que la secuencia que finalmente recibe LEAN sea correcta.

Aquí el problema fundamental es:

```text
¿Podemos garantizar que el stream observable por LEAN
contiene exactamente los puntos correctos,
en el orden correcto,
sin pérdidas ni duplicados?
```

Este bloque reúne principalmente:

- política de overlaps y deduplicación entre archivos;
- comportamiento durante History;
- warmup → normal;
- `LastPointTracker`;
- fill-forward;
- timezone/DST;
- cruces entre archivos y entre estados del motor.

El reader aislado puede funcionar perfectamente y aun así existir un problema cuando ese stream atraviesa los wrappers y lifecycles de LEAN.

## Qué tenemos que resolver

La deduplicación actual funciona mediante una política streaming similar a:

```text
último timestamp emitido
```

Esto es correcto cuando los archivos solapados contienen duplicados equivalentes.

Pero puede perder información si dos archivos solapados son complementarios.

Antes de cambiar el algoritmo debemos conocer el contrato real de Data Engineering:

```text
¿los overlaps son copias/supersets coherentes
o pueden contener gaps complementarios?
```

Paralelamente debemos demostrar que los lifecycles nativos utilizados por el backend binario no introducen duplicados, huecos o cambios semánticos.

## Preguntas que deben quedar respondidas

- ¿Qué garantía ofrece Data Engineering sobre archivos solapados?
- ¿Un timestamp repetido entre archivos representa siempre el mismo dato?
- ¿Puede un archivo posterior contener puntos antiguos que faltaban en el anterior?
- Si los overlaps están garantizados como duplicados coherentes, ¿first-file-wins es el contrato oficial?
- Si no existe esa garantía, ¿qué merge streaming necesitamos?
- ¿Cuánto lookahead sería suficiente sin romper la propiedad de memoria acotada?
- ¿Qué invariantes debe mantener el stream al pasar de warmup a normal?
- ¿Cómo verificamos ausencia de duplicados o gaps alrededor de `LastPointTracker`?
- ¿Fill-forward observa exactamente la granularidad real del dataset?
- ¿Las conversiones UTC → timezone de exchange siguen siendo correctas atravesando DST?
- ¿History y DataFeed producen semánticamente el mismo dato cuando reciben requests equivalentes?

## Ideas iniciales para discutir

Antes de diseñar un merge más complejo, conviene determinar si realmente es necesario.

Si Data Engineering garantiza formalmente:

```text
overlap = datos idénticos o coherentes
```

la solución más simple probablemente sea conservar first-file-wins, documentarlo como invariante y protegerlo con tests.

Si esa garantía no existe, entonces sí tendría sentido diseñar un merge cronológico limitado.

Independientemente de esa decisión, necesitamos una pequeña matriz end-to-end que compruebe explícitamente:

```text
file boundary
warmup → normal
History
fill-forward
DST
```

porque estas pruebas protegen la integración con LEAN, no únicamente los readers binarios.

---

# Problema 4 — Cerrar rendimiento, validación y mantenibilidad del pipeline

Los tres problemas anteriores son principalmente de corrección y contrato.

Este cuarto bloque empieza una vez que esas decisiones estén cerradas.

Agrupa:

- acceso ineficiente a zonas profundas de un archivo;
- benchmark controlado old-vs-new;
- sincronización final con upstream;
- limpieza de documentación temporal;
- squash/fixup del patch stack.

No todos estos puntos requieren una nueva arquitectura.

Son el proceso necesario para poder declarar estable esta iteración.

## Qué tenemos que resolver

El reader actual mantiene memoria acotada, pero una request situada cerca del final de un archivo puede obligarlo a recorrer secuencialmente todos los registros previos.

Como los archivos tienen:

```text
record size fijo
+
timestamps ordenados
```

podría localizarse el primer registro relevante mediante binary search, seek directo o un índice.

Sin embargo, esta optimización no debería implementarse por intuición. Primero necesitamos medir si tiene impacto suficiente.

Igualmente, sabemos estructuralmente que el pipeline streaming evita la retención `O(N)` del loader anterior, pero todavía no existe una comparación controlada old-vs-new que permita cuantificar la mejora.

Finalmente, después de cerrar la implementación debemos transformar el conjunto de commits y documentos temporales en un patch limpio y mantenible sobre upstream LEAN.

## Preguntas que deben quedar respondidas

- ¿Cuánto coste real genera actualmente escanear desde el inicio del archivo?
- ¿Necesitamos optimizarlo ya o puede quedar como mejora futura?
- Si se optimiza, ¿binary search directo sobre timestamps es suficiente?
- ¿Necesitamos realmente un índice persistente?
- ¿Qué benchmark mínimo consideramos suficiente para comparar loader antiguo y streaming?
- ¿Qué métricas serán criterio de aceptación?
- ¿Qué documentación pertenece permanentemente al fork y qué documentos son únicamente historial de ingeniería?
- ¿Cuándo hacemos el último sync/rebase contra upstream?
- ¿Qué suite debe pasar después de ese sync?
- ¿Cuándo estamos suficientemente seguros para hacer fixup/squash del patch stack?

## Ideas iniciales para discutir

La optimización del seek debería quedar subordinada al profiling.

La alternativa más simple, si resulta necesaria, parece ser aprovechar directamente que:

```text
offset = recordIndex * recordSize
```

y realizar búsqueda binaria sobre timestamps sin introducir todavía infraestructura adicional.

El benchmark debería utilizar exactamente:

```text
mismo dataset
mismo rango
mismo workload
mismo hardware
```

comparando el loader anterior contra el pipeline streaming.

Las métricas principales deberían incluir al menos:

```text
elapsed time
time-to-first-bar
managed heap
working set / private bytes
GC
allocations
```

Finalmente, solo después de que los tres bloques de corrección estén cerrados y la suite crítica pase debería realizarse la limpieza del patch stack.

---

# Orden propuesto de discusión

Los cuatro problemas tienen dependencia entre sí.

El orden lógico sería:

```text
1. Binary Data Contract
        ↓
2. LEAN Request Resolution
        ↓
3. Stream & Lifecycle Integrity
        ↓
4. Performance & Engineering Closure
```

No conviene decidir el Problema 2 sin haber cerrado el Problema 1, porque no podemos resolver correctamente una request si todavía no hemos establecido qué representa cada dataset físico.

Del mismo modo, no tiene sentido optimizar el Problema 4 antes de estar seguros de que los comportamientos definidos en los Problemas 1–3 son los definitivos.

---

# Resultado esperado de la discusión

Para cada uno de los cuatro problemas queremos terminar produciendo algo de esta forma:

```text
DECISIÓN
Qué comportamiento elegimos.

RAZÓN
Por qué elegimos esa política.

INVARIANTES
Qué cosas deben ser siempre verdaderas.

COMPORTAMIENTO ESPERADO
Qué hará el pipeline en los casos válidos.

ERROR POLICY
Qué hará en los casos incompatibles o ambiguos.

CASOS QUE DEBEN FUNCIONAR
Escenarios obligatorios de aceptación.

CASOS QUE DEBEN FALLAR
Escenarios que deben rechazarse explícitamente.

TESTS NECESARIOS
Cómo demostramos que la decisión está correctamente implementada.
```

Esos cuatro bloques resueltos serán entonces la materia prima del nuevo SDD.

El siguiente SDD no deberá pedirle a Codex que decida ninguna de estas políticas.

Deberá recibirlas ya cerradas y limitarse a implementarlas, validarlas y reportar los resultados.