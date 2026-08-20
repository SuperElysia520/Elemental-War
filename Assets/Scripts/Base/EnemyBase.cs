using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.UI;

public enum EnemyState
{
    Idle,
    Move,
    Attack,
    Dead
}

/// <summary>
/// 敌人基类
/// </summary>
public abstract class EnemyBase : MonoBehaviour, IStateMachineOwner
{
    [HideInInspector] public Animator animator;
    protected StateMachine stateMachine;

    #region 寻路相关
    [HideInInspector] public NavMeshAgent navMeshAgent; // 寻路代理
    [Tooltip("转向速度")] public float rotationSpeed = 300f;
    [Tooltip("最小攻击距离")] public float minAttackDistance = 1f; // 敌人在最短攻击距离处停下攻击
    [HideInInspector] public PlayerModel attackTarget;
    #endregion

    #region 流血相关的预制体
    [Tooltip("喷血溅射特效")] public GameObject bloodSmashPrefab;
    [Tooltip("滴血特效")] public GameObject bloodDrippingPrefab;
    #endregion

    #region 受击相关
    protected int hitHash;
    protected int moveSpeedHash;
    protected float normalMoveSpeed = 1;
    protected float slowMoveSpeed = 0.5f;
    protected Coroutine recoverSpeedCoroutine; // 恢复速度的协程
    #endregion

    #region 血条相关
    [Tooltip("生命值")] public int health = 100;
    private float currentHealth;
    private bool isDead = false;
    [Tooltip("血条预制体")] public GameObject healthBarPrefab;
    [Tooltip("血条的生成位置")] public Transform healthBarPos;
    [HideInInspector] public GameObject healthBar; // 实例化后的血条

    public float healthBarShowTime = 5;
    private float healthBarShow_timer;
    #endregion

    protected virtual void Awake()
    {
        stateMachine = new StateMachine(this);
        animator = GetComponent<Animator>();
        navMeshAgent = GetComponent<NavMeshAgent>();
        navMeshAgent.stoppingDistance = minAttackDistance;
        navMeshAgent.angularSpeed = rotationSpeed;
        hitHash = Animator.StringToHash("Hit");
        moveSpeedHash = Animator.StringToHash("MoveSpeed");
        currentHealth = health;
        healthBarShow_timer = healthBarShowTime;
    }

    protected virtual void Start()
    {
        SwitchState(EnemyState.Idle);
        FindAttackTarget();

        #region 实例化血条框
        healthBar = Instantiate(healthBarPrefab, healthBarPos.position, Quaternion.identity);
        healthBar.transform.SetParent(UIManager.instance.WorldSpaceCanvas.transform);
        #endregion
    }

    protected virtual void Update()
    {
        if (isDead) return;
        
        #region 血条相关
        if (healthBarShow_timer < healthBarShowTime)
        {
            healthBar.SetActive(true);
            healthBar.transform.position = healthBarPos.transform.position;
            healthBarShow_timer += Time.deltaTime;
        }
        else
        {
            healthBar.SetActive(false);
        }
        #endregion
    }

    /// <summary>
    /// 寻找离自身最近的PlayerModel
    /// </summary>
    public virtual void FindAttackTarget()
    {
        PlayerModel[] playerModels = GameManager.instance.playerModels;
        if (playerModels != null && playerModels.Length > 0)
        {
            PlayerModel closestPlayer = null;
            float minDistance = float.MaxValue;
            foreach (PlayerModel player in playerModels)
            {
                if (player != null)
                {
                    float distance = Vector3.Distance(transform.position, player.transform.position);
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        closestPlayer = player;
                    }
                }
            }
            // 设置攻击目标
            attackTarget =  closestPlayer;
        }
    }

    /// <summary>
    /// 减慢移动动画播放速度，持续一段时间后恢复
    /// </summary>
    protected virtual void slowMoveAnimation()
    {
        animator.SetFloat(moveSpeedHash, slowMoveSpeed);
        // 保证每次受击的时候减速协程不会被上一个减速协程影响
        if (recoverSpeedCoroutine != null)
            StopCoroutine(recoverSpeedCoroutine);
        recoverSpeedCoroutine = StartCoroutine(RecoverMoveSpeed(0.5f));
    }

    /// <summary>
    /// 恢复速度
    /// </summary>
    protected virtual IEnumerator RecoverMoveSpeed(float delay)
    {
        // 等待指定时间
        yield return new WaitForSeconds(delay);
        // 恢复正常移动速度
        animator.SetFloat(moveSpeedHash, normalMoveSpeed);
        recoverSpeedCoroutine = null;
    }

    /// <summary>
    /// 受击
    /// </summary>
    public virtual void Hurt(PlayerWeapnBullet bullet, float damageMultiplier = 1)
    {
        #region 受击动画相关
        animator.SetTrigger(hitHash);
        slowMoveAnimation();
        #endregion
        
        #region 生成喷血特效
        // 计算子弹的方向
        Vector3 bulletDir = bullet.transform.position;
        // 根据子弹的方向计算旋转
        Quaternion rotation = Quaternion.LookRotation(-bulletDir);
        // 生成喷血特效
        Destroy(Instantiate(bloodDrippingPrefab, bullet.transform.position, rotation), 3);
        #endregion

        #region 生成流血滴落特效
        Destroy(Instantiate(bloodDrippingPrefab,transform.position + Vector3.up * 0.1f, Quaternion.Euler(0, 0, 0)), 3);
        #endregion

        #region 血条相关
        currentHealth -= bullet.damage * damageMultiplier;
        if (currentHealth > 0)
        {
            healthBarShow_timer = 0;
            // 更新血条
            healthBar.GetComponent<EnemyHealthBarUI>().UpdateHealthBar(currentHealth / health);
        }
        else
        {
            SwitchState(EnemyState.Dead);
            navMeshAgent.enabled = false;
            GetComponent<BoxCollider>().enabled = false;
            currentHealth = 0;
            isDead = true;
            Destroy(healthBar); //销毁血条
        }
        #endregion
    }

    /// <summary>
    /// Hitscan 射线命中入口。
    /// </summary>
    public virtual void Hurt(float damage, Vector3 hitPoint, Vector3 shotDirection)
    {
        if (isDead || damage <= 0f)
            return;

        if (animator != null)
        {
            animator.SetTrigger(hitHash);
            slowMoveAnimation();
        }

        Quaternion hitRotation = shotDirection.sqrMagnitude > 0.000001f
            ? Quaternion.LookRotation(-shotDirection.normalized)
            : Quaternion.identity;
        if (bloodSmashPrefab != null)
            Destroy(Instantiate(bloodSmashPrefab, hitPoint, hitRotation), 3f);
        if (bloodDrippingPrefab != null)
            Destroy(Instantiate(bloodDrippingPrefab, transform.position + Vector3.up * 0.1f, Quaternion.identity), 3f);

        currentHealth -= damage;
        if (currentHealth > 0f)
        {
            healthBarShow_timer = 0f;
            if (healthBar != null)
            {
                EnemyHealthBarUI healthBarUI = healthBar.GetComponent<EnemyHealthBarUI>();
                if (healthBarUI != null)
                    healthBarUI.UpdateHealthBar(currentHealth / health);
            }
            return;
        }

        currentHealth = 0f;
        isDead = true;
        SwitchState(EnemyState.Dead);
        if (navMeshAgent != null)
            navMeshAgent.enabled = false;
        BoxCollider bodyCollider = GetComponent<BoxCollider>();
        if (bodyCollider != null)
            bodyCollider.enabled = false;
        if (healthBar != null)
            Destroy(healthBar);
    }

    /// <summary>
    /// 是否存在攻击目标
    /// </summary>
    /// <returns></returns>
    public virtual bool HasAttackTarget()
    {
        return attackTarget !=  null;
    }

    /// <summary>
    /// 攻击目标是否在最短攻击范围内
    /// </summary>
    /// <returns></returns>
    public virtual bool IsAtttackTargetInAttackRange()
    {
        if (HasAttackTarget())
        {
            return Vector3.Distance(transform.position, attackTarget.transform.position) < minAttackDistance;
        }
        return false;
    }

    /// <summary>
    /// 追击目标
    /// </summary>
    public virtual void ChaseTarget()
    {
        if (HasAttackTarget())
        {
            navMeshAgent.SetDestination(attackTarget.transform.position);
        }
    }
        
        
    /// <summary>
    /// 切换状态
    /// </summary>
    public abstract void SwitchState(EnemyState state);

    /// <summary>
    /// 播放动画
    /// </summary>
    /// <param name="animationName">动画名称</param>
    /// <param name="transition">过渡时间</param>
    /// <param name="layer">动画层</param>
    public void PlayStateAnimation(string animationName, float transition = 0.25f, int layer = 0)
    {
        animator.CrossFadeInFixedTime(animationName, transition, layer); // 控制播放动画平滑淡出，目标动画平滑淡入
    }
    
    // 销毁敌人
    public void Clear()
    {
        stateMachine.Stop();
        Destroy(gameObject);
    }
}
