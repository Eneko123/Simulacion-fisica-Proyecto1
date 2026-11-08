using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class SimpleSpacetime : MonoBehaviour
{
    // ====================================================================== PARAMETROS

    [Header("Prefabs")]
    public GameObject cubePrefab;      // cubo de la malla 
    public GameObject spherePrefab;    // planeta 

    [Header("UI (Canvas)")]
    public Slider massSlider;
    public Slider diameterSlider;
    public Slider anchorSlider;
    public Slider couplingSlider;
    public Slider dampingSlider;
    public Toggle diagonalsToggle;
    public Button resetButton;
    public TMP_Text infoText;          // texto que muestra los valores y las medidas

    [Header("Malla")]
    public int n = 25;                 // numero de cubos por lado (n x n)
    public float size = 12.5f;         // lado total de la malla (en metros)

    [Header("Muelles")]
    public float anchorK = 10f;        // rigidez al reposo: cuanto tira cada cubo de volver a su altura inicial
    public float couplingK = 40f;      // rigidez entre vecinos: cuanto se arrastran los cubos entre si
    public float damping = 25f;        // amortiguamiento: frena el movimiento (evita que rebote sin parar)
    public bool diagonals = true;      // true = cada cubo se une a 8 vecinos, false = solo a 4

    [Header("Planetas")]
    public float planetMass = 5f;
    public float planetDiameter = 1f; 
    public float contactSoftening = 1.5f;  // cuantas celdas "extra" reparten el peso alrededor del planeta
    public float spawnHeight = 4f;     // altura desde la que cae el planeta

    [Header("Visual")]
    public float dilation = 0.5f;      // cuanto se ensanchan las celdas al hundirse

    // Valores fijos
    const float NodeMass = 1f;         // masa de cada cubo
    const float MaxSpeed = 10f;        // velocidad maxima de un cubo (evita inestabilidades)
    const float MaxDepth = 12f;        // hundimiento maximo
    const float HorizonDepth = 5f;     // profundidad a la que el reloj se pararia
    const float ColorGain = 5f;        // cuanto se nota el cambio de color con la profundidad

    // ====================================================================== DATOS INTERNOS

    int N;                             // tamaño real de la malla (N x N)
    float spacing;                     // separacion entre cubos
    Transform[,] cubes;                // cada cubo de la malla
    Rigidbody[,] bodies;               // su Rigidbody (para moverlo con la fisica)
    Renderer[,] rends;                 // su Renderer (para cambiarle el color)

    // Matrices N x N con un valor por cubo:
    float[,] y;                        // altura actual (0 = reposo, negativo = hundido)
    float[,] y2;                       // copia donde se calcula la altura nueva
    float[,] v;                        // velocidad vertical
    float[,] f;                        // fuerza que ejercen los planetas
    float[,] w;                        // pesos temporales
    float[,] posX, sizeX;              // posicion X y ancho de cada celda (para la dilatacion)
    float[,] restX, restZ;             // posicion original (X, Z) de cada cubo

    MaterialPropertyBlock mpb = null;  // permite dar un color distinto a cada cubo sin crear materiales
    readonly List<Rigidbody> planets = new List<Rigidbody>();   // planetas que hay en la escena

    // Medidas que se muestran en el panel
    float depthMax, wellRadius, minRate = 1f;

    // ====================================================================== INICIO

    void Start()
    {
        BuildGrid();

        // Conectamos el panel: cada slider fija su rango, su valor inicial y que variable modifica
        Bind(massSlider, 1f, 200f, planetMass, x => planetMass = x);
        Bind(diameterSlider, 0.2f, 2.5f, planetDiameter, x => planetDiameter = x);
        Bind(anchorSlider, 1f, 200f, anchorK, x => anchorK = x);
        Bind(couplingSlider, 1f, 200f, couplingK, x => couplingK = x);
        Bind(dampingSlider, 0f, 100f, damping, x => damping = x);
        diagonalsToggle.isOn = diagonals;
        diagonalsToggle.onValueChanged.AddListener(x => diagonals = x);
        resetButton.onClick.AddListener(ResetSim);
    }

    // Configura un slider: rango (min-max), valor inicial y que hacer cuando el usuario lo mueve
    void Bind(Slider s, float min, float max, float value, UnityAction<float> onChange)
    {
        s.minValue = min; s.maxValue = max; s.value = value;
        s.onValueChanged.AddListener(onChange);
    }

    // ====================================================================== CREAR LA MALLA

    void BuildGrid()
    {
        // Borra la malla anterior (si la hay)
        for (int k = transform.childCount - 1; k >= 0; k--) Destroy(transform.GetChild(k).gameObject);

        // Prepara las matrices
        N = Mathf.Max(5, n);
        spacing = size / N;
        cubes = new Transform[N, N]; bodies = new Rigidbody[N, N]; rends = new Renderer[N, N];
        y = new float[N, N]; y2 = new float[N, N]; v = new float[N, N]; f = new float[N, N];
        w = new float[N, N]; posX = new float[N, N]; sizeX = new float[N, N];
        restX = new float[N, N]; restZ = new float[N, N];
        mpb = new MaterialPropertyBlock();

        // Coloca un cubo en cada posicion de la cuadricula, centrada en este objeto
        float half = (N - 1) * spacing / 2f;
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                restX[i, j] = transform.position.x + i * spacing - half;
                restZ[i, j] = transform.position.z + j * spacing - half;
                GameObject c = Instantiate(cubePrefab, new Vector3(restX[i, j], transform.position.y, restZ[i, j]),
                                           Quaternion.identity, transform);
                c.transform.localScale = new Vector3(spacing, 0.2f, spacing);
                cubes[i, j] = c.transform; bodies[i, j] = c.GetComponent<Rigidbody>(); rends[i, j] = c.GetComponent<Renderer>();
            }
    }

    // ====================================================================== SIMULACION

    // Se ejecuta a intervalos fijos (0,02 s). Aqui ocurre toda la fisica de la malla.
    void FixedUpdate()
    {
        planets.RemoveAll(p => p == null);      // quita planetas que ya no existan
        ApplyPlanetLoad();                      // 1. fuerzas de los planetas
        Integrate(Time.fixedDeltaTime);         // 2. mover los cubos con muelles
        ApplyToCubes();                         // 3. actualizar posicion, tamaño y color
    }

    // cada planeta reparte su peso (masa x gravedad) entre los cubos que tiene debajo.
    // los cubos mas cercanos al centro del planeta reciben mas peso. La suma total es exactamente m·g.
    void ApplyPlanetLoad()
    {
        System.Array.Clear(f, 0, f.Length);     // pone todas las fuerzas a 0
        float g = Physics.gravity.magnitude;

        foreach (Rigidbody p in planets)
        {
            Vector3 pp = p.position;
            float radius = p.transform.localScale.x / 2f;
            float influence = radius + contactSoftening * spacing;   // hasta donde llega su efecto
            float sum = 0f;

            // calcula cuanto "toca" el planeta a cada cubo (w): 1 en el centro, 0 en el borde
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    w[i, j] = 0f;
                    if (pp.y - radius > transform.position.y + y[i, j] + 0.4f) continue;  // aun no ha llegado a la malla
                    float dx = pp.x - restX[i, j], dz = pp.z - restZ[i, j];
                    float d = Mathf.Sqrt(dx * dx + dz * dz);                              // distancia horizontal
                    if (d >= influence) continue;                                         // demasiado lejos
                    float q = 1f - d / influence;
                    w[i, j] = q * q;
                    sum += w[i, j];
                }
            if (sum <= 0f) continue;

            // reparte el peso total en proporcion a w (el signo negativo = hacia abajo)
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                    f[i, j] -= p.mass * g * w[i, j] / sum;
        }
    }

    // calcula la nueva altura de cada cubo.
    // sobre cada cubo actuan 4 fuerzas: muelle al reposo, muelles a los vecinos, peso del planeta y rozamiento.
    void Integrate(float dt)
    {
        // Con diagonales hay mas muelles, asi que se reduce la rigidez de cada uno para que
        // la tela sea igual de rigida con 4 u 8 vecinos
        float kc = couplingK * (diagonals ? 1f / (1f + Mathf.Sqrt(2f)) : 1f);
        float kd = diagonals ? kc / Mathf.Sqrt(2f) : 0f;       // la diagonal es mas larga, asi que es mas blanda
        float damp = 1f + damping / NodeMass * dt;             // rozamiento

        for (int i = 1; i < N - 1; i++)                        // el borde de la malla queda fijo
            for (int j = 1; j < N - 1; j++)
            {
                float yc = y[i, j];

                // Fuerza total sobre el cubo:
                //  - anchorK:   tira del cubo hacia su altura de reposo
                //  - f:         empuje de los planetas
                //  - kc:        tira hacia la altura de los 4 vecinos (arriba, abajo, izquierda, derecha)
                float force = -anchorK * yc + f[i, j]
                            + kc * ((y[i - 1, j] - yc) + (y[i + 1, j] - yc) + (y[i, j - 1] - yc) + (y[i, j + 1] - yc));

                //  - kd:        lo mismo con las 4 diagonales (si estan activadas)
                if (diagonals)
                    force += kd * ((y[i - 1, j - 1] - yc) + (y[i - 1, j + 1] - yc)
                                 + (y[i + 1, j - 1] - yc) + (y[i + 1, j + 1] - yc));

                // Euler semiimplicito: primero se actualiza la velocidad y con ella la posicion
                v[i, j] = Mathf.Clamp((v[i, j] + force / NodeMass * dt) / damp, -MaxSpeed, MaxSpeed);
                y2[i, j] = Mathf.Clamp(yc + v[i, j] * dt, -MaxDepth, 0.5f);
            }

        // Intercambia las matrices: y2 (la nueva) pasa a ser y (la actual)
        float[,] tmp = y; y = y2; y2 = tmp;
    }

    // aplica el resultado a los cubos (posicion, tamaño, color) y calcula las medidas.
    void ApplyToCubes()
    {
        Vector3 basePos = transform.position;
        float L = N * spacing, left = basePos.x - L / 2f, back = basePos.z - L / 2f;

        // Dilatacion del patron
        // Cada celda tiene un "ancho relativo" w: 1 si esta en reposo, mayor si esta hundida.
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
                w[i, j] = 1f + dilation * Mathf.Clamp01(-y[i, j]);

        // En cada fila, el ancho total L se reparte entre las celdas en proporcion a w.
        // Asi las celdas del pozo se ensanchan, las demas se estrechan un poco y nunca se solapan.
        for (int j = 0; j < N; j++)
        {
            float sum = 0f, cum = 0f;
            for (int i = 0; i < N; i++) sum += w[i, j];
            for (int i = 0; i < N; i++)
            {
                posX[i, j] = left + L * (cum + w[i, j] * 0.5f) / sum;   // centro de la celda en X
                sizeX[i, j] = L * w[i, j] / sum;                        // ancho de la celda en X
                cum += w[i, j];
            }
        }

        // Lo mismo en cada columna (eje Z), y se aplica todo a los cubos
        float minY = 0f;
        for (int i = 0; i < N; i++)
        {
            float sum = 0f, cum = 0f;
            for (int j = 0; j < N; j++) sum += w[i, j];
            for (int j = 0; j < N; j++)
            {
                float zc = back + L * (cum + w[i, j] * 0.5f) / sum;     // centro de la celda en Z
                float sz = L * w[i, j] / sum;                           // ancho de la celda en Z
                cum += w[i, j];

                // Mueve y escala el cubo (la altura viene de y)
                bodies[i, j].MovePosition(new Vector3(posX[i, j], basePos.y + y[i, j], zc));
                cubes[i, j].localScale = new Vector3(sizeX[i, j], 0.2f, sz);

                // Color: la linea pasa de blanco a naranja y a rojo oscuro cuanto mas lento va el reloj
                float t = (1f - ClockRate(-y[i, j])) * ColorGain;
                Color c = t < 1f ? Color.Lerp(Color.white, new Color(1f, 0.45f, 0.05f), t)
                                 : Color.Lerp(new Color(1f, 0.45f, 0.05f), new Color(0.7f, 0.05f, 0f), Mathf.Clamp01(t - 1f));
                mpb.SetColor("_Color", c);       // para el render Built-in
                mpb.SetColor("_BaseColor", c);   // para URP
                rends[i, j].SetPropertyBlock(mpb);

                if (y[i, j] < minY) minY = y[i, j];     // guarda el punto mas profundo
            }
        }

        // Medidas para el panel
        depthMax = -minY;                                // profundidad maxima del pozo

        // Radio del pozo: se cuentan los cubos hundidos mas de la mitad de la profundidad maxima y se calcula el radio de un circulo con esa misma area.
        int count = 0;
        if (depthMax > 0.01f)
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                    if (-y[i, j] > 0.5f * depthMax) count++;
        wellRadius = Mathf.Sqrt(count * spacing * spacing / Mathf.PI);

        minRate = ClockRate(depthMax);                   // ritmo del reloj en el punto mas profundo
    }

    // Ritmo del reloj segun la profundidad: 1 (100 %) en reposo, 0 al llegar a HorizonDepth.
    float ClockRate(float depth) { return Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Max(0f, depth) / HorizonDepth)); }

    // ====================================================================== INTERACCIoN

    void Update()
    {
        // Actualiza el texto del panel con los valores y las medidas
        infoText.text = $"Masa: {planetMass:F1} \nDiametro: {planetDiameter:F2} m\n" +
                        $"Rigidez reposo: {anchorK:F1} \nVecinos: {couplingK:F1} \nAmort.: {damping:F1}\n" +
                        $"Profundidad max: {depthMax:F2} m\nRadio del pozo: {wellRadius:F2} m\n" +
                        $"Ritmo del reloj minimo: {minRate * 100f:F1} %";

        // Clic izquierdo = crear un planeta
        if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame) return;
        if (EventSystem.current.IsPointerOverGameObject()) return;

        // Lanza un rayo desde la camara hasta un plano horizontal a la altura de caida
        Plane plane = new Plane(Vector3.up, transform.position + Vector3.up * spawnHeight);
        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (plane.Raycast(ray, out float dist)) SpawnPlanet(ray.GetPoint(dist));
    }

    // Crea un planeta en la posicion p con la masa y el diametro actuales del panel
    void SpawnPlanet(Vector3 p)
    {
        GameObject sp = Instantiate(spherePrefab, p, Quaternion.identity);   // copia del prefab en la escena
        sp.transform.localScale = Vector3.one * planetDiameter;
        sp.GetComponent<Renderer>().material.color = Color.HSVToRGB(Random.value, 0.7f, 1f);  // color al azar

        Rigidbody rb = sp.GetComponent<Rigidbody>();
        rb.mass = planetMass;
        planets.Add(rb);
    }

    // Boton "Reiniciar": borra los planetas y reconstruye la malla (aplica el valor actual de n y size)
    void ResetSim()
    {
        foreach (Rigidbody p in planets) if (p) Destroy(p.gameObject);
        planets.Clear();
        BuildGrid();
    }
}