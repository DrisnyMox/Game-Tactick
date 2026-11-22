using UnityEngine;

/// <summary>
/// Кость рэгдолла, отброшенного взрывом: ударилась о блоки арены быстрее CharacterHealth.BreakSpeed —
/// выбивает их шаром и летит дальше, а не отскакивает. Работает, пока тело летит после взрыва
/// (CharacterHealth.FlyingAfterBlast): обычное падение уничтоженного стены не ломает.
/// CharacterHealth вешает компонент на каждую кость в Awake и включает на время полёта.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class RagdollImpact : MonoBehaviour
{
    // Пока кость пробивает блоки, следующий удар — не раньше чем через столько секунд:
    // коллайдер чанка пересобирается не в тот же шаг физики, и кость бьётся о старый
    const float BreakCooldown = 0.08f;
    // Какую долю скорости до удара кость сохраняет, пробив блоки
    const float KeepSpeed = 0.6f;

    CharacterHealth owner;
    Rigidbody body;
    Vector3 velocityBeforeStep;
    float nextBreak;

    public void Init(CharacterHealth character)
    {
        owner = character;
        body = GetComponent<Rigidbody>();
        enabled = false;
    }

    // FixedUpdate идёт перед шагом физики: здесь скорость ещё до удара, в OnCollisionEnter — уже после отскока
    void FixedUpdate()
    {
        velocityBeforeStep = body.linearVelocity;
    }

    // Unity шлёт OnCollisionEnter и выключенному компоненту, поэтому проверка enabled здесь нужна
    void OnCollisionEnter(Collision collision)
    {
        if (!enabled || Time.time < nextBreak)
            return;
        float speed = collision.relativeVelocity.magnitude;
        if (speed < owner.BreakSpeed)
            return;
        var arena = collision.collider.GetComponentInParent<VoxelArena>();
        if (arena == null)
            return;

        // Центр шара чуть за точкой касания, в глубине блока: от середины кости через точку касания
        var contact = collision.GetContact(0).point;
        var into = (contact - body.worldCenterOfMass).normalized;
        float strength = Mathf.InverseLerp(owner.BreakSpeed, owner.BreakSpeed * 2.5f, speed);
        float radius = Mathf.Lerp(owner.BreakRadius, owner.BreakRadius * 1.7f, strength) * arena.VoxelSize;
        if (arena.Explode(contact + into * (arena.VoxelSize * 0.5f), radius) == 0)
            return;
        nextBreak = Time.time + BreakCooldown;
        body.linearVelocity = velocityBeforeStep * KeepSpeed;
    }
}
